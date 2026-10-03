using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using PerfAgent.Collectors;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PerfAgent.Core
{
    /// <summary>
    /// 编辑器自身每帧托管开销的基线测量。
    ///
    /// <para><b>为什么必须有它</b></para>
    /// 「每帧托管分配」用的是 <c>ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame")</c>，
    /// 这个计数器统计的是**整个编辑器进程**当帧的托管分配 —— 里面包含 Inspector / SceneView / GUI、
    /// Profiler 窗口的记录开销，以及本工具自己的采样回调与界面刷新。
    ///
    /// 实证：同一个空工程、同一个场景，两次采集的 P50 分别是 <b>16871 B</b> 与 <b>97409 B</b>，
    /// 差 6 倍 —— 差的就是编辑器状态（窗口是否打开、Profiler 是否在记录）。
    /// 拿这个数字去跟「播放器每帧分配预算（默认 2048 B）」比，会把一个**空工程**报成严重问题。
    /// 那是工具自己制造的假问题，不是项目的问题。
    ///
    /// <para><b>做法</b></para>
    /// 在编辑模式下空转 ~0.4 秒（Profiler 保持记录，与本工具抓帧时一致），用同一个计数器采一批样本，
    /// 取中位数作为「编辑器开销基线」。之后所有对每帧分配的判断都改成看「实测 − 基线」，
    /// 并且两个数字都写进快照，让使用者自己也能核验。
    ///
    /// <para><b>边界（如实说明，不猜）</b></para>
    /// 基线是在**编辑模式**下测的，而采集发生在 Play 模式，两者的编辑器开销不会完全一致，
    /// 所以「项目每帧分配」是**估算值**，规则侧还会额外要求它明显越过基线的噪声带才敢下结论。
    /// 已经在 Play 里、或取不到计数器时，基线一律记为不可用 —— 这时宁可不报，也不把编辑器开销算到项目头上。
    /// </summary>
    public static class EditorOverheadBaseline
    {
        const string ValueKey = "PerfAgent.Baseline.GcAllocPerFrame";
        const string ValidKey = "PerfAgent.Baseline.GcAllocPerFrame.Valid";
        const string SamplesKey = "PerfAgent.Baseline.GcAllocPerFrame.Samples";
        const string ReasonKey = "PerfAgent.Baseline.GcAllocPerFrame.Reason";

        // 窗口宽一点：编辑模式下 EditorApplication.update 会被降频，
        // 窗口太短会「样本不足」而直接丢掉基线（实测就是这么丢的）。
        /// <summary>
        /// 基线窗口：至少录到这么多帧（或 recorder 样本）就够，最多等 MaxMs 毫秒。
        /// 4 秒而不是 1.5 秒 —— 编辑模式的帧率本来就低，编译/导入还会再压一档。
        /// </summary>
        const int TargetFrames = 20;
        const int MinFrames = 5;
        const double MaxMs = 4000;

        /// <summary>基线值（B/帧）。NaN = 不可用。</summary>
        public static double BytesPerFrame = double.NaN;

        /// <summary>参与中位数的样本数。</summary>
        public static int Samples;

        /// <summary>是否正在测量。</summary>
        public static bool Measuring { get; private set; }

        /// <summary>不可用时的人类可读原因。</summary>
        public static string Reason = "";

        public static void Invalidate()
        {
            BytesPerFrame = double.NaN;
            Samples = 0;
            Reason = "";
            try
            {
                SessionState.EraseBool(ValidKey);
                SessionState.EraseFloat(ValueKey);
                SessionState.EraseInt(SamplesKey);
                SessionState.EraseString(ReasonKey);
            }
            catch { }
        }

        /// <summary>
        /// 取基线。静态字段在域重载后会丢，所以这里会回落到 SessionState（会话级，跨域重载有效）。
        /// 返回值表示是否可用；不可用时给出原因，调用方据此写「本次不归因」的说明。
        /// </summary>
        public static bool TryGet(out double bytesPerFrame, out string reason)
        {
            if (double.IsNaN(BytesPerFrame))
            {
                try
                {
                    if (SessionState.GetBool(ValidKey, false))
                    {
                        double v = SessionState.GetFloat(ValueKey, 0f);
                        if (v > 0) { BytesPerFrame = v; Samples = SessionState.GetInt(SamplesKey, 0); }
                    }
                    // 失败原因也要持久化：它是静态字段，进 Play 时的域重载会把它清掉，
                    // 只剩一句「没测过基线」，看不出到底为什么没测到。
                    if (string.IsNullOrEmpty(Reason)) Reason = SessionState.GetString(ReasonKey, "");
                }
                catch { }
            }

            if (!double.IsNaN(BytesPerFrame) && BytesPerFrame > 0)
            {
                bytesPerFrame = BytesPerFrame;
                reason = "";
                return true;
            }

            bytesPerFrame = double.NaN;
            reason = string.IsNullOrEmpty(Reason) ? "本次采集前没有测过基线" : Reason;
            return false;
        }

        /// <summary>
        /// 异步测一次基线；无论成功与否都会调用 onDone。
        ///
        /// 读数优先走 **Profiler 面板序列**（与采集数据同一口径）；面板拿不到时退回
        /// **ProfilerRecorder 的 LastValue 样本**（同一个计数器的兜底口径，日志里会写明用的哪个）。
        ///
        /// 非 Play 模式下必须同时满足两个前提，否则样本必然是空的：
        ///   1. <c>ProfilerDriver.enabled</c> = true（否则根本不记录）；
        ///   2. <c>ProfilerDriver.profileEditor</c> = true —— 只开 (1) 的话面板**不记录编辑器帧**，
        ///      <c>lastFrameIndex</c> 不推进，读出来就是「0 帧」（实测踩过）。
        /// 两个开关用完都还原成用户原来的状态。
        /// </summary>
        public static void Measure(Action onDone)
        {
            if (Measuring)
            {
                if (onDone != null) onDone();
                return;
            }

            if (EditorApplication.isPlaying)
            {
                Invalidate();
                Reason = "当前已经在 Play 模式里，测不到编辑模式的空闲基线";
                if (onDone != null) onDone();
                return;
            }

            if (PerfSession.Capturing)
            {
                // 采集期间加测量动作会污染正在采的数据，直接放弃
                Invalidate();
                Reason = "已有采集任务在进行，不在此时插入测量";
                if (onDone != null) onDone();
                return;
            }

            Invalidate();
            Measuring = true;

            bool profilerWasEnabled = ProfilerApi.Enabled;
            bool profileEditorWas = ProfilerApi.ProfileEditor;

            if (!profilerWasEnabled) ProfilerApi.Enabled = true;
            if (!profileEditorWas) ProfilerApi.ProfileEditor = true;

            // 从这里之后录进来的帧就是「编辑器空转」的帧
            int startFrame = ProfilerApi.LastFrameIndex;
            var clock = Stopwatch.StartNew();

            // 点亮 GC 分配计数器：不订阅它就不会逐帧记录，序列读出来是空的
            var recorder = StatRecorder.Register(ProfilerCategory.Memory, StatRecorder.GcAllocCounters);
            var live = new List<double>(TargetFrames + 8);

            EditorApplication.CallbackFunction tick = null;

            Action<int> finish = delegate (int endFrame)
            {
                EditorApplication.update -= tick;
                if (!profilerWasEnabled) ProfilerApi.Enabled = false;
                if (!profileEditorWas) ProfilerApi.ProfileEditor = false;

                // endFrame > 0：测量被「进入 Play」打断 —— 只读打断之前那段编辑模式帧，
                // 之后的帧是 Play 帧，不是「编辑器空转」，算进来会把基线抬高。
                int last = endFrame > 0 ? endFrame : ProfilerApi.LastFrameIndex;
                int count = (startFrame >= 0 && last > startFrame) ? last - startFrame : 0;

                double median = 0;
                int valid = 0;
                string used = ReadPanelSeries(startFrame, count, out median, out valid);
                if (median <= 0) used = ReadRecorderSamples(live, out median, out valid);

                if (median > 0)
                {
                    BytesPerFrame = median;
                    Samples = valid;
                    Reason = "";
                    try
                    {
                        SessionState.SetFloat(ValueKey, (float)median);
                        SessionState.SetBool(ValidKey, true);
                        SessionState.SetInt(SamplesKey, valid);
                        SessionState.SetString(ReasonKey, "");
                    }
                    catch { }

                    Debug.Log("[PerfAgent] 编辑器开销基线 = " + ((long)median).ToString(CultureInfo.InvariantCulture)
                              + " B/帧（" + used + "，" + valid + " 个样本的中位数）。采集时会从「每帧托管分配」里扣掉它。");
                }
                else
                {
                    Reason = DescribeFailure(count, live.Count);
                    try { SessionState.SetString(ReasonKey, Reason); } catch { }
                    Debug.LogWarning("[PerfAgent] 编辑器开销基线测量失败：" + Reason);
                }

                StatRecorder.Dispose(ref recorder);
                Measuring = false;
                if (onDone != null) onDone();
            };

            tick = delegate
            {
                // 有人进了 Play（或开始了采集）：到此为止，只用之前的编辑模式帧（够就成基线，不够就作废）
                if (EditorApplication.isPlaying || PerfSession.Capturing)
                {
                    finish(ProfilerApi.LastFrameIndex);
                    return;
                }

                if (StatRecorder.Has(recorder))
                {
                    long v = StatRecorder.Last(recorder);
                    if (v > 0) live.Add(v);
                }

                int last = ProfilerApi.LastFrameIndex;
                int count = (startFrame >= 0 && last > startFrame) ? last - startFrame : 0;

                if (count < TargetFrames && live.Count < TargetFrames && clock.Elapsed.TotalMilliseconds <= MaxMs)
                    return;

                finish(-1);
            };

            EditorApplication.update += tick;
        }

        /// <summary>读面板序列取中位数；返回口径描述（空串 = 不可用）。</summary>
        static string ReadPanelSeries(int startFrame, int count, out double median, out int valid)
        {
            median = 0;
            valid = 0;
            if (count < MinFrames) return "";

            var values = new float[count];
            for (int i = 0; i < StatRecorder.GcAllocCounters.Length; i++)
            {
                if (!ProfilerApi.ReadCounterSeries("Memory", StatRecorder.GcAllocCounters[i], startFrame + 1, values))
                    continue;

                var samples = new List<double>(count);
                for (int k = 0; k < count; k++)
                    if (values[k] > 0f) samples.Add(values[k]);
                if (samples.Count < MinFrames) continue;

                samples.Sort();
                median = samples[samples.Count / 2];
                valid = samples.Count;
                return "面板序列 Memory/" + StatRecorder.GcAllocCounters[i];
            }

            return "";
        }

        /// <summary>
        /// 兜底口径：每帧读一次 recorder 的 LastValue（与面板 GC Alloc 列同源）。
        /// 只在面板序列拿不到时使用 —— 它要读值，所以不属于「只挂订阅不读」的点亮用法。
        /// </summary>
        static string ReadRecorderSamples(List<double> samples, out double median, out int valid)
        {
            median = 0;
            valid = 0;
            if (samples == null || samples.Count < MinFrames) return "";

            var copy = new List<double>(samples);
            copy.Sort();
            median = copy[copy.Count / 2];
            valid = copy.Count;
            return "ProfilerRecorder/Memory GC Allocated In Frame（面板序列不可用时的兜底）";
        }

        /// <summary>失败原因要说清卡在哪一步，否则界面上只剩一句「没测到基线」看不出所以然。</summary>
        static string DescribeFailure(int panelFrames, int recorderSamples)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "测量期间编辑器正在编译/导入资源，没有产生空转帧（面板 " + panelFrames
                     + " 帧、recorder " + recorderSamples + " 个样本）";

            return "编辑器空转期间没拿到有效样本：面板录到 " + panelFrames + " 帧、recorder 采到 "
                 + recorderSamples + " 个（都要求至少 " + MinFrames + " 个）；"
                 + "通常是编辑器主循环没跑起来（窗口失焦被降频、Profiler 被手动暂停、正在编译）";
        }
    }
}

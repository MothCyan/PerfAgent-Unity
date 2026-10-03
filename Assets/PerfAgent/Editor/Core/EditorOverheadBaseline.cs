using System;
using System.Globalization;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using PerfAgent.Collectors;

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
        const int TargetSamples = 20;
        const int MinSamples = 5;
        const int WarmupTicks = 3;
        const double MaxSeconds = 1.5;

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
        /// 异步测一次基线；无论成功与否都会调用 onDone。测量本身不分配（固定数组 + 静态委托），
        /// 否则量的就是自己。
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
            if (!profilerWasEnabled) ProfilerApi.Enabled = true;

            var rec = StatRecorder.MakeFirstValid(ProfilerCategory.Memory, StatRecorder.GcAllocCounters);
            var samples = new double[TargetSamples];
            int n = 0;
            int warmup = WarmupTicks;
            double start = EditorApplication.timeSinceStartup;

            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                bool finished = !rec.Valid
                                || n >= TargetSamples
                                || EditorApplication.timeSinceStartup - start > MaxSeconds;

                if (!finished)
                {
                    if (warmup > 0) warmup--;
                    else
                    {
                        long v = StatRecorder.Last(rec);
                        if (v > 0) samples[n++] = v;
                    }
                    return;
                }

                EditorApplication.update -= tick;
                try { if (rec.Valid) rec.Dispose(); } catch { }
                if (!profilerWasEnabled) ProfilerApi.Enabled = false;

                if (!rec.Valid)
                {
                    Reason = "本机取不到 GC Allocated In Frame 计数器，无法测编辑器开销基线";
                }
                else if (n < MinSamples)
                {
                    Reason = "编辑器空转期间只取到 " + n + " 个样本，不足以确定基线";
                }
                else
                {
                    double median = Median(samples, n);
                    if (median > 0)
                    {
                        BytesPerFrame = median;
                        Samples = n;
                        Reason = "";
                        try
                        {
                            SessionState.SetFloat(ValueKey, (float)median);
                            SessionState.SetBool(ValidKey, true);
                            SessionState.SetInt(SamplesKey, n);
                            SessionState.SetString(ReasonKey, "");
                        }
                        catch { }
                    }
                    else
                    {
                        Reason = "编辑器空转期间的分配样本全为 0，基线不可用";
                    }
                }

                if (!string.IsNullOrEmpty(Reason))
                {
                    try { SessionState.SetString(ReasonKey, Reason); } catch { }
                    Debug.LogWarning("[PerfAgent] 编辑器开销基线测量失败：" + Reason);
                }
                else
                {
                    Debug.Log("[PerfAgent] 编辑器开销基线 = " + ((long)BytesPerFrame).ToString(CultureInfo.InvariantCulture)
                              + " B/帧（" + Samples + " 个样本）。采集时会从「每帧托管分配」里扣掉它。");
                }

                Measuring = false;
                if (onDone != null) onDone();
            };

            EditorApplication.update += tick;
        }

        static double Median(double[] values, int count)
        {
            if (count <= 0) return double.NaN;
            var copy = new double[count];
            Array.Copy(values, copy, count);
            Array.Sort(copy);
            return count % 2 == 1 ? copy[count / 2] : (copy[count / 2 - 1] + copy[count / 2]) * 0.5;
        }
    }
}

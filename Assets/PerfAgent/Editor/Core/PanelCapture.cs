using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using PerfAgent.Collectors;

namespace PerfAgent.Core
{
    /// <summary>
    /// 「采一段帧数据」—— 但**不再自己逐帧采样**，只做三件事：
    ///   1. 让 Profiler 处于记录状态（面板得先录下来，才有数据可读）；
    ///   2. 记住窗口起止的面板帧号；
    ///   3. 结束时把面板记录的这段帧读出来（<see cref="ProfilerPanel"/>）。
    ///
    /// 为什么删掉自采样（原来的 FrameCapture）：
    ///   - 采样回调本身就在给编辑器加开销，而它要测的正是「每帧开销」——等于自己污染自己的测量；
    ///   - 跟随采集原上限 100 万帧，要一直维护内存缓冲；
    ///   - 帧耗时是 Stopwatch 量编辑器 update 间隔，把编辑器自身的停顿也算进去了；
    ///     面板给的是引擎自己测的帧耗时，还能直接读 PlayerLoop 行的总耗时（不含编辑器开销）。
    ///
    /// 保留的轮询只是「每 0.25 秒看一眼面板录到第几帧」，用于进度与结束条件，
    /// 不再逐帧读计数器、不再每帧分配对象。
    /// </summary>
    public class PanelCapture
    {
        /// <summary>目标帧数（0 = 不限，只按时长/手动结束）。</summary>
        public int targetFrames = 300;

        /// <summary>&gt;0 表示按时间结束：跑够这么多秒就停，targetFrames 退化为安全上限。</summary>
        public double durationSeconds;

        /// <summary>丢弃窗口开头这么多秒（进入 Play 的启动抖动）。0 = 不丢。</summary>
        public double warmupSeconds;

        public bool running { get; private set; }

        /// <summary>面板已记录的帧数（进度显示用；不是采样数）。</summary>
        public int CapturedCount { get; private set; }

        /// <summary>窗口起点（面板帧号）；-1 = 还没开始。</summary>
        public int startFrame { get; private set; }

        readonly Action<PanelCaptureData> _onDone;
        readonly Action<float> _onProgress;
        readonly Stopwatch _clock = new Stopwatch();

        bool _profilerWasEnabled;
        bool _restoreProfilerState;
        double _nextPoll;
        int _pollIntervalMs = 250;

        /// <summary>
        /// 只用来「点亮」GC 分配计数器的订阅（不读它的值）。
        /// 这个计数器只在有人订阅时才会逐帧记录，否则面板序列是空的。
        /// </summary>
        ProfilerRecorder _gcAllocRegistration;

        public PanelCapture(int frames, Action<PanelCaptureData> onDone, Action<float> onProgress = null)
        {
            targetFrames = Mathf.Max(0, frames);
            _onDone = onDone;
            _onProgress = onProgress;
        }

        /// <summary>按时长采集；frames 作为安全上限，防止长时间无上限地录下去。</summary>
        public PanelCapture(int frames, double seconds, Action<PanelCaptureData> onDone, Action<float> onProgress = null)
        {
            targetFrames = Mathf.Max(0, frames);
            durationSeconds = seconds > 0 ? seconds : 0;
            _onDone = onDone;
            _onProgress = onProgress;
        }

        public void Start()
        {
            if (running) return;

            running = true;
            startFrame = -1;
            CapturedCount = 0;

            // 面板在记录，才有帧可读。用户自己开着 Profiler 时不改动他的设置。
            _profilerWasEnabled = ProfilerApi.Enabled;
            if (!_profilerWasEnabled)
            {
                ProfilerApi.Enabled = true;
                _restoreProfilerState = true;
            }

            // 窗口从「现在之后的第一帧」开始（这里之后录进来的都是被测期间）
            startFrame = ProfilerApi.LastFrameIndex;

            // 点亮 GC 分配计数器：不订阅的话它不会逐帧记录，面板序列读出来是空的
            // （实测 300 帧里只有 1 帧有值）。只挂订阅，不读值 —— 不是逐帧采样。
            _gcAllocRegistration = StatRecorder.Register(ProfilerCategory.Memory, StatRecorder.GcAllocCounters);

            _clock.Restart();
            _nextPoll = 0;
            EditorApplication.update += Poll;
        }

        public void Cancel()
        {
            Stop(false);
        }

        /// <summary>
        /// 立即结束并触发回调（用户点停止、退出 Play、或按帧数/时长跑够）。
        /// 回调是同步的 —— 退出 Play 那一帧必须立刻把数据读完，否则域销毁后就没机会了。
        /// </summary>
        public void FinishNow()
        {
            Stop(true);
        }

        void Stop(bool invokeCallback)
        {
            if (!running) return;

            running = false;
            EditorApplication.update -= Poll;
            _clock.Stop();

            if (_restoreProfilerState)
            {
                ProfilerApi.Enabled = false;
                _restoreProfilerState = false;
            }

            if (!invokeCallback || _onDone == null)
            {
                StatRecorder.Dispose(ref _gcAllocRegistration);
                return;
            }

            int last = ProfilerApi.LastFrameIndex;
            PanelCaptureData data = startFrame < 0
                ? new PanelCaptureData { unavailableReason = "采集还没开始就结束了" }
                : ProfilerPanel.Read(startFrame + 1, last, ProfilerPanel.MaxSamples, warmupSeconds);

            StatRecorder.Dispose(ref _gcAllocRegistration);
            _onDone(data);
        }

        void Poll()
        {
            if (!running) return;

            // 只是看一眼面板进度，不读任何逐帧数据
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now < _nextPoll) return;
            _nextPoll = now + _pollIntervalMs;

            int last = ProfilerApi.LastFrameIndex;
            CapturedCount = (startFrame < 0 || last < startFrame) ? 0 : last - startFrame;

            if (_onProgress != null)
            {
                _onProgress(durationSeconds > 0
                    ? (float)Math.Min(1.0, _clock.Elapsed.TotalSeconds / durationSeconds)
                    : (targetFrames > 0 ? (float)CapturedCount / targetFrames : 0f));
            }

            bool timeUp = durationSeconds > 0 && _clock.Elapsed.TotalSeconds >= durationSeconds;
            bool framesUp = targetFrames > 0 && CapturedCount >= targetFrames;
            if (timeUp || framesUp) Stop(true);
        }

        // =====================================================================
        // 汇总成指标
        // =====================================================================

        /// <summary>
        /// 把面板数据汇总成快照指标。
        ///
        /// 口径说明（都会写进 source 与 notes，可以逐项核验）：
        ///   帧耗时  → 面板抽样帧的 PlayerLoop 总耗时（不含编辑器开销）
        ///   每帧分配 → 面板 GC Allocated In Frame 序列，**整段窗口每一帧**（不是抽样）
        ///   Draw Call → 面板 Draw Calls Count 序列，整段窗口
        /// </summary>
        public static void Summarize(PerfSnapshot s, PanelCaptureData data)
        {
            if (s == null) return;

            if (data == null || !data.available)
            {
                s.AddNote("没有可用的 Profiler 面板数据："
                          + (data == null ? "采集未产出数据" : data.unavailableReason)
                          + "。请确认抓帧期间 Profiler 处于记录状态（工具会自动打开），"
                          + "并让面板历史保留住这段帧（历史长度可在 Profiler 窗口设置）。");
                return;
            }

            if (data.notes != null)
            {
                for (int i = 0; i < data.notes.Count; i++) s.AddNote(data.notes[i]);
            }

            s.capturedFrameCount = data.frameCount;
            s.frames = new List<FrameStat>(data.samples);

            s.AddNote(string.Format(CultureInfo.InvariantCulture,
                "数据来自 Profiler 面板的帧历史：窗口 {0}~{1}（共 {2} 帧），逐帧明细抽样 {3} 帧；"
                + "整段序列（分配 / Draw Call 等）覆盖窗口内每一帧。",
                data.firstFrame, data.lastFrame, data.frameCount, data.samples.Count));

            if (data.warmupFramesDropped > 0)
            {
                s.AddNote("已丢弃窗口开头 " + data.warmupFramesDropped
                          + " 帧（约 " + data.warmupSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                          + " 秒）的启动抖动：域重载、首次 Shader 编译与资源初始化都在那里。");
            }

            // ---- 帧耗时（来自抽样帧；百分比只代表抽样）----
            //
            // 闸门：面板列语义是反推的，一旦把别的列当成耗时列，会算出几十亿 ms 这种荒谬值；
            // 宁可没有帧耗时，也不把垃圾读数变成「严重：帧耗时超预算」。
            int implausible = 0;
            for (int i = 0; i < data.samples.Count; i++)
                if (!ProfilerPanel.IsPlausibleFrameMs(data.samples[i].deltaMs)) implausible++;
            if (implausible > 0)
            {
                data.samples.RemoveAll(delegate (FrameStat f) { return !ProfilerPanel.IsPlausibleFrameMs(f.deltaMs); });
                s.AddNote("丢弃了 " + implausible + " 帧不合理的帧耗时读数（超出 0~"
                          + ProfilerPanel.MaxPlausibleFrameMs.ToString("0", CultureInfo.InvariantCulture)
                          + " ms）：面板列语义与预期不一致，已按读不到处理。可运行 API 探针查看列内容。");
            }

            if (data.samples.Count == 0)
                s.AddNote("没有可用的帧耗时读数：本次不给帧耗时相关指标与结论（帧率类判定全部跳过）。");

            if (data.samples.Count > 0)
            {
                string src = "Profiler 面板抽样 " + data.samples.Count + "/" + data.frameCount + " 帧";
                var sorted = new List<double>(data.samples.Count);
                double sum = 0, max = 0;
                for (int i = 0; i < data.samples.Count; i++)
                {
                    double d = data.samples[i].deltaMs;
                    sorted.Add(d);
                    sum += d;
                    if (d > max) max = d;
                }
                sorted.Sort();

                double avg = sum / data.samples.Count;
                var m = s.SetMetric("帧耗时均值", "ms", avg, src);
                m.min = sorted[0];
                m.max = max;
                m.avg = avg;
                m.samples = data.samples.Count;

                SetPercentile(s, sorted, "帧耗时 P50", 50, src);
                SetPercentile(s, sorted, "帧耗时 P95", 95, src);
                SetPercentile(s, sorted, "帧耗时 P99", 99, src);
                s.SetMetric("帧耗时峰值", "ms", max, src);
                s.SetMetric("实际 FPS", "fps", 1000.0 / Math.Max(0.0001, avg), src);
            }

            // ---- 每帧托管分配（整段窗口的序列）----
            var alloc = data.gcAlloc;
            if (alloc != null && alloc.readable && alloc.validCount > 0)
            {
                var rm = s.SetMetric("每帧托管分配", "B", alloc.mean,
                    "Profiler 面板序列 Memory/GC Allocated In Frame（与面板 GC Alloc 列同源，覆盖窗口内每一帧）");
                rm.samples = alloc.validCount;
                rm.min = alloc.p50;
                rm.max = alloc.max;

                s.SetMetric("每帧分配 P50", "B", alloc.p50,
                    "Profiler 面板序列 Memory/GC Allocated In Frame");
                s.SetMetric("每帧分配 P95", "B", alloc.p95,
                    "Profiler 面板序列 Memory/GC Allocated In Frame");

                ApplyEditorBaseline(s);
            }
            else
            {
                s.AddNote("面板的 GC 分配序列不可用"
                          + (alloc == null || string.IsNullOrEmpty(alloc.error) ? "" : "（" + alloc.error + "）")
                          + "：本次不给「每帧托管分配」结论。");
            }

            // ---- 渲染统计（整段窗口的序列）----
            var dc = data.drawCalls;
            if (dc != null && dc.readable && dc.validCount > 0)
            {
                s.SetMetric("Draw Calls 均值", "次", dc.mean,
                    "Profiler 面板序列 Render/Draw Calls Count（整段窗口）");
                s.SetMetric("Draw Calls 峰值", "次", dc.max,
                    "Profiler 面板序列 Render/Draw Calls Count（整段窗口）");
            }
            if (data.setPass != null && data.setPass.readable && data.setPass.validCount > 0)
                s.SetMetric("SetPass Calls", "次", data.setPass.mean,
                    "Profiler 面板序列 Render/SetPass Calls Count（整段窗口）");
            if (data.triangles != null && data.triangles.readable && data.triangles.validCount > 0)
                s.SetMetric("Triangles", "个", data.triangles.mean,
                    "Profiler 面板序列 Render/Triangles Count（整段窗口）");
            if (data.textureMemory != null && data.textureMemory.readable && data.textureMemory.validCount > 0)
                s.SetMetric("纹理内存", "B", data.textureMemory.max,
                    "Profiler 面板序列 Memory/Texture Memory（取窗口内峰值）");

            s.AddNote("逐帧「GC 次数」与「TempAllocator 增长」两个口径已随自采样一起移除："
                      + "Profiler 面板不提供这两个序列（面板只有分配量、没有 GC 事件计数）。"
                      + "想知道 GC 何时发生，请看帧耗时尖峰与「每帧分配 P95」——分配高的帧就是最可能触发 GC 的帧；"
                      + "TempAllocator 的当前值仍由内存采集器给出。");
        }

        /// <summary>
        /// 「每帧托管分配」扣掉编辑器自身开销。
        ///
        /// 这个计数器统计的是**整个编辑器进程**当帧的托管分配，含 Inspector / GUI / Profiler 记录
        /// 与本工具自身开销；不扣就会把一个空工程报成「严重超预算」。
        /// </summary>
        static void ApplyEditorBaseline(PerfSnapshot s)
        {
            var raw = s.FindMetric("每帧托管分配");
            if (raw == null || double.IsNaN(raw.value)) return;

            double baseline;
            string why;
            if (!EditorOverheadBaseline.TryGet(out baseline, out why))
            {
                s.AddNote(string.Format(CultureInfo.InvariantCulture,
                    "本次没有可用的「编辑器开销基线」（{0}）：实测 {1:0} B/帧 里含编辑器自身的开销，"
                    + "无法区分出项目贡献 —— 因此本次不对「每帧托管分配」下任何结论（宁可不说，也不把编辑器开销算到项目头上）。",
                    why, raw.value));
                return;
            }

            s.SetMetric("编辑器开销基线", "B", baseline,
                "编辑模式空转实测（含编辑器与工具自身开销；" + EditorOverheadBaseline.Samples + " 个样本的中位数）");

            double residual = raw.value - baseline;
            if (residual < 0) residual = 0;
            s.SetMetric("项目每帧分配", "B", residual, "估算：每帧托管分配 − 编辑器开销基线");

            s.AddNote(string.Format(CultureInfo.InvariantCulture,
                "每帧托管分配已分口径：实测 {0:0} B/帧（含编辑器自身开销）− 编辑器基线 {1:0} B/帧 = 项目自身约 {2:0} B/帧。"
                + "基线在编辑模式测、采集在 Play 模式，残差是估算值；只有它明显超过预算且越过基线噪声带才会被当成项目问题。",
                raw.value, baseline, residual));

            if (residual <= 0)
                s.AddNote("每帧托管分配与编辑器空闲基线相当 —— 这部分是编辑器自身的开销，不计为项目问题。");
        }

        static void SetPercentile(PerfSnapshot s, List<double> sorted, string label, int percentile, string source)
        {
            int idx = (int)Math.Round((percentile / 100.0) * (sorted.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            s.SetMetric(label, "ms", sorted[idx], source);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using PerfAgent.Core;

namespace PerfAgent.Collectors
{
    /// <summary>
    /// 逐帧采样器。
    ///
    /// 托管分配刻意采两路，因为两者的口径不一样：
    ///  1. <b>ProfilerRecorder「GC Allocated In Frame」</b> —— 与 Unity Profiler 窗口的 GC Alloc 列同源，
    ///     统计当帧真实分配总量，是主口径；
    ///  2. <b>GC.GetTotalMemory 差值</b> —— 只能看到堆的净增长，看不见「分配后立即被回收」的部分，会低估，
    ///     仅作为交叉校验；两者偏差超过 30% 时会在快照里明确写出差异。
    ///
    /// 计数器名字对不上时 Valid == false，一律跳过、不写 0：宁可没有数据，也不给错数据。
    /// </summary>
    public class FrameCapture
    {
        /// <summary>GC 分配计数器的候选名（跨版本有差异，第一个有效者胜）。</summary>
        internal static readonly string[] GcAllocCounters = { "GC Allocated In Frame", "GC Alloc" };

        public int targetFrames = 300;

        /// <summary>
        /// >0 表示按**时间**采集：跑够这么多秒就停，targetFrames 退化为安全上限。
        ///
        /// 为「走完一遍完整游戏流程」这类需求准备 —— 流程长度以秒计，不以帧计。
        /// 用帧数描述「打完一局」既不准（帧率变了就不是同一段时间）也难算（用户得自己换算）。
        /// </summary>
        public double durationSeconds;

        /// <summary>
        /// 落盘时最多保留多少帧。原始帧仍**全部**参与统计，这里只影响快照文件的体积。
        /// 3000 帧在帧耗时曲线上已经足够看出形态，再多人眼也分辨不出来。
        /// </summary>
        public const int MaxStoredFrames = 3000;

        public bool running { get; private set; }

        /// <summary>已采到的帧数。外部（如自动 Play 模式测试的状态机）用它汇报进度。</summary>
        public int CapturedCount { get { return _frames.Count; } }

        readonly List<FrameStat> _frames = new List<FrameStat>();
        readonly Stopwatch _stopwatch = new Stopwatch();
        readonly Action<List<FrameStat>> _onDone;
        readonly Action<float> _onProgress;

        int _lastFrame = -1;
        long _lastManaged;
        double _lastStopwatchMs;

        ProfilerRecorder _drawCalls, _batches, _setPass, _triangles, _gcAllocInFrame;
        bool _restoreProfilerState;
        bool _profilerWasEnabled;

        public FrameCapture(int frames, Action<List<FrameStat>> onDone, Action<float> onProgress = null)
        {
            targetFrames = Mathf.Max(10, frames);
            _onDone = onDone;
            _onProgress = onProgress;
        }

        /// <summary>按时长采集；frames 作为安全上限，防止极端低帧率下无限采下去。</summary>
        public FrameCapture(int frames, double seconds, Action<List<FrameStat>> onDone, Action<float> onProgress = null)
        {
            targetFrames = Mathf.Max(10, frames);
            durationSeconds = seconds > 0 ? seconds : 0;
            _onDone = onDone;
            _onProgress = onProgress;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            _frames.Clear();
            _lastFrame = -1;
            _lastManaged = GC.GetTotalMemory(false);

            // 抓帧期间必须让 Profiler 开着，否则 ProfilerRecorder 拿不到数据
            _profilerWasEnabled = ProfilerApi.Enabled;
            if (!_profilerWasEnabled)
            {
                ProfilerApi.Enabled = true;
                _restoreProfilerState = true;
            }

            _drawCalls = StatRecorder.Make(ProfilerCategory.Render, "Draw Calls Count");
            _batches = StatRecorder.Make(ProfilerCategory.Render, "Batches Count");
            _setPass = StatRecorder.Make(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = StatRecorder.Make(ProfilerCategory.Render, "Triangles Count");
            _gcAllocInFrame = StatRecorder.MakeFirstValid(ProfilerCategory.Memory, GcAllocCounters);

            _stopwatch.Restart();
            _lastStopwatchMs = 0;
            EditorApplication.update += Tick;
        }

        public void Cancel()
        {
            Stop(false);
        }

        /// <summary>
        /// 立即结束并触发回调。
        ///
        /// 用于「用户自己操作完」这种**外部驱动**的结束（点了停止、退出了 Play），
        /// 与按帧数 / 按时长自动结束等价，只是结束条件来自外部。
        /// 回调是同步的 —— 退出 Play 那一帧必须立刻把数据落盘，否则域销毁后就没机会了。
        /// </summary>
        public void FinishNow()
        {
            Stop(true);
        }

        void Stop(bool invokeCallback)
        {
            if (!running) return;
            running = false;
            EditorApplication.update -= Tick;
            _stopwatch.Stop();

            DisposeRecorder(ref _drawCalls);
            DisposeRecorder(ref _batches);
            DisposeRecorder(ref _setPass);
            DisposeRecorder(ref _triangles);
            DisposeRecorder(ref _gcAllocInFrame);

            if (_restoreProfilerState)
            {
                ProfilerApi.Enabled = false;
                _restoreProfilerState = false;
            }

            if (invokeCallback && _onDone != null) _onDone(_frames);

            // 长采集时这个列表可能有几万条（几十 MB），回调已经用完了，立即释放。
            // 前提：Summarize 已经把它拷进了快照 —— 见那边的说明。
            _frames.Clear();
        }

        static void DisposeRecorder(ref ProfilerRecorder r)
        {
            if (r.Valid) { try { r.Dispose(); } catch { } }
        }

        void Tick()
        {
            if (!running) return;

            int frame = Time.frameCount;
            if (frame == _lastFrame) return;   // 同一帧内多次 update，跳过

            double nowMs = _stopwatch.Elapsed.TotalMilliseconds;
            double deltaMs = nowMs - _lastStopwatchMs;
            _lastStopwatchMs = nowMs;
            _lastFrame = frame;

            long managed = GC.GetTotalMemory(false);
            long alloc = managed - _lastManaged;
            _lastManaged = managed;

            var f = new FrameStat();
            f.frame = frame;
            f.deltaMs = deltaMs;
            f.managedAllocBytes = alloc;
            f.allocInFrameBytes = StatRecorder.Last(_gcAllocInFrame);
            f.tempAllocBytes = MemApi.TempAllocator;
            f.totalMemoryBytes = MemApi.TotalAllocated;
            f.drawCalls = (int)StatRecorder.Last(_drawCalls);
            f.batches = (int)StatRecorder.Last(_batches);
            f.setPassCalls = (int)StatRecorder.Last(_setPass);
            f.triangles = StatRecorder.Last(_triangles);
            _frames.Add(f);

            if (_onProgress != null && _frames.Count % 10 == 0)
            {
                // 按时长采集时进度按时间算，否则进度条会跟着帧率忽快忽慢
                _onProgress(durationSeconds > 0
                    ? (float)Math.Min(1.0, _stopwatch.Elapsed.TotalSeconds / durationSeconds)
                    : (float)_frames.Count / targetFrames);
            }

            bool timeUp = durationSeconds > 0 && _stopwatch.Elapsed.TotalSeconds >= durationSeconds;
            if (timeUp || _frames.Count >= targetFrames)
                Stop(true);
        }

        /// <summary>把采样结果汇总成指标。</summary>
        public static void Summarize(PerfSnapshot s, List<FrameStat> frames)
        {
            if (s == null || frames == null || frames.Count == 0)
            {
                if (s != null) s.AddNote("未采到任何帧数据。");
                return;
            }

            // 原始采样总量单独记一个字段：s.frames 可能只是抽取后的子集，
            // 界面与 MCP 若直接读 frames.Count，会把 18000 帧显示成 3000 帧。
            s.capturedFrameCount = frames.Count;

            // 必须拷一份：采集结束时会释放原始列表，而 Downsample 在帧数不超上限时
            // 会原样返回同一个引用 —— 不拷的话快照里存的列表会被跟着清空。
            s.frames = new List<FrameStat>(FrameStats.Downsample(frames, MaxStoredFrames));
            if (s.frames.Count < frames.Count)
            {
                s.AddNote(string.Format(CultureInfo.InvariantCulture,
                    "本次共采集 {0} 帧。为控制快照体积，逐帧明细按均匀间隔抽取为 {1} 帧；" +
                    "所有统计指标（均值 / 分位 / 峰值）均基于全部 {0} 帧计算，不受抽取影响。",
                    frames.Count, s.frames.Count));
            }

            var sorted = new List<double>(frames.Count);
            double sum = 0, max = 0, min = double.MaxValue;
            for (int i = 0; i < frames.Count; i++)
            {
                double d = frames[i].deltaMs;
                sorted.Add(d);
                sum += d;
                if (d > max) max = d;
                if (d < min) min = d;
            }
            sorted.Sort();

            double avg = sum / frames.Count;
            double targetFps = s.targetFrameRate > 0 ? s.targetFrameRate : 60.0;
            double frameBudget = 1000.0 / targetFps;

            var m = s.SetMetric("帧耗时均值", "ms", avg, "FrameCapture/" + frames.Count + " 帧");
            m.min = min; m.max = max; m.avg = avg; m.samples = frames.Count;
            m.severity = Severity.FromRatio(avg, frameBudget * 0.8, frameBudget);

            SetP(s, sorted, "帧耗时 P50", 50, frames.Count);
            SetP(s, sorted, "帧耗时 P95", 95, frames.Count);
            SetP(s, sorted, "帧耗时 P99", 99, frames.Count);
            s.SetMetric("帧耗时峰值", "ms", max, "FrameCapture");
            s.SetMetric("实际 FPS", "fps", 1000.0 / Math.Max(0.0001, avg), "FrameCapture");

            int gcEvents = 0;
            double allocSum = 0; int allocCount = 0;
            long tempMax = 0;
            int dcMax = 0, dcSum = 0, dcCount = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.managedAllocBytes < 0) gcEvents++;
                else { allocSum += f.managedAllocBytes; allocCount++; }
                if (f.tempAllocBytes > tempMax) tempMax = f.tempAllocBytes;
                if (f.drawCalls > dcMax) dcMax = f.drawCalls;
                if (f.drawCalls > 0) { dcSum += f.drawCalls; dcCount++; }
            }

            // ---- 每帧托管分配：优先 ProfilerRecorder 口径，GC.GetTotalMemory 差值只做交叉校验 ----
            int recorderSamples = 0;
            double recorderAvg = s.AvgRecorderAllocPerFrame();
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].allocInFrameBytes > 0) recorderSamples++;

            double diffAvg = allocCount == 0 ? 0 : allocSum / allocCount;

            if (!double.IsNaN(recorderAvg))
            {
                var rm = s.SetMetric("每帧托管分配", "B", recorderAvg,
                    "ProfilerRecorder: GC Allocated In Frame（与 Profiler 窗口 GC Alloc 同源）");
                rm.samples = recorderSamples;

                // 两路口径都拿到时，差异过大要写出来，而不是默默选一个
                if (allocCount > 0)
                {
                    double deviation = Math.Abs(recorderAvg - diffAvg) / Math.Max(1.0, recorderAvg);
                    if (deviation > 0.3)
                    {
                        s.AddNote(string.Format(CultureInfo.InvariantCulture,
                            "托管分配的两个口径差异较大（{0:P0}）：ProfilerRecorder = {1:0} B/帧，GC.GetTotalMemory 差值 = {2:0} B/帧。" +
                            "已采用 ProfilerRecorder —— 差值法看不见「当帧分配后立即被回收」的部分，会低估。",
                            deviation, recorderAvg, diffAvg));
                    }
                }
            }
            else
            {
                var dm = s.SetMetric("每帧托管分配", "B", diffAvg,
                    "GC.GetTotalMemory 差值（降级口径：不含当帧分配后即回收的部分，可能低估）");
                dm.samples = allocCount;
                s.AddNote("未取到 ProfilerRecorder 的 GC 分配计数器，托管分配已降级为 GC.GetTotalMemory 差值 —— " +
                          "该口径会低估分配量，结论的严重度判断可能偏乐观。请运行 API 探针确认计数器可用性。");
            }

            // ---- 归因：把编辑器自身的每帧开销从「每帧托管分配」里剥出来 ----
            //
            // 必须剥的原因：GC Allocated In Frame 统计的是**整个编辑器进程**当帧的托管分配，
            // 包含 Inspector / SceneView / GUI、Profiler 记录，以及本工具自己的采样与界面刷新。
            // 实测：同一个空工程两次采集的 P50 分别是 16871 B 与 97409 B（差 6 倍）——
            // 差的是编辑器状态，不是项目的分配。不剥就会把空工程报成严重问题。
            var perFrameAlloc = s.FindMetric("每帧托管分配");
            if (perFrameAlloc != null && !double.IsNaN(perFrameAlloc.value))
            {
                double baseline;
                string baselineWhy;
                if (EditorOverheadBaseline.TryGet(out baseline, out baselineWhy))
                {
                    s.SetMetric("编辑器开销基线", "B", baseline,
                        "编辑模式空转实测（含编辑器与工具自身开销；" + EditorOverheadBaseline.Samples + " 个样本的中位数）");

                    double residual = perFrameAlloc.value - baseline;
                    if (residual < 0) residual = 0;
                    s.SetMetric("项目每帧分配", "B", residual, "估算：每帧托管分配（实测）− 编辑器开销基线");

                    s.AddNote(string.Format(CultureInfo.InvariantCulture,
                        "每帧托管分配已分口径：实测 {0:0} B/帧（含编辑器自身开销）− 编辑器基线 {1:0} B/帧 = 项目自身约 {2:0} B/帧。"
                        + "基线是编辑模式下空转实测的，而采集发生在 Play 模式，两者开销不会完全一致，因此「项目每帧分配」是估算值；"
                        + "只有它明显超过预算且超出基线噪声带时才会被当成项目问题。",
                        perFrameAlloc.value, baseline, residual));

                    if (residual <= 0)
                        s.AddNote("每帧托管分配与编辑器空闲基线相当 —— 这部分是编辑器自身的开销，不计为项目问题。");
                }
                else
                {
                    s.AddNote(string.Format(CultureInfo.InvariantCulture,
                        "本次没有可用的「编辑器开销基线」（{0}）：实测 {1:0} B/帧 里含编辑器自身的开销，"
                        + "无法区分出项目贡献 —— 因此本次不对「每帧托管分配」下任何结论（宁可不说，也不把编辑器开销算到项目头上）。",
                        baselineWhy, perFrameAlloc.value));
                }
            }

            s.SetMetric("GC 次数", "次", gcEvents, "采样窗口内 GC.GetTotalMemory 回退次数");
            s.SetMetric("TempAllocator 峰值", "B", tempMax, "Profiler.GetTempAllocatorSize 峰值");
            if (dcCount > 0)
            {
                s.SetMetric("Draw Calls 峰值", "次", dcMax, "ProfilerRecorder 峰值");
                s.SetMetric("Draw Calls 均值", "次", (double)dcSum / dcCount, "ProfilerRecorder 均值");
            }
        }

        static void SetP(PerfSnapshot s, List<double> sorted, string label, int percentile, int count)
        {
            int idx = (int)Math.Round((percentile / 100.0) * (count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            s.SetMetric(label, "ms", sorted[idx], "FrameCapture");
        }
    }
}

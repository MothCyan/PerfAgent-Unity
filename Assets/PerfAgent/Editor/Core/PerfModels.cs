using System;
using System.Collections.Generic;
using PerfAgent.Core;

namespace PerfAgent.Core
{
    public static class Severity
    {
        public const string Info = "info";
        public const string Warn = "warn";
        public const string Error = "error";

        public static int Rank(string s)
        {
            if (s == Error) return 3;
            if (s == Warn) return 2;
            return 1;
        }

        public static string FromRatio(double value, double warnRatio, double errorRatio)
        {
            if (errorRatio > 0 && value >= errorRatio) return Error;
            if (warnRatio > 0 && value >= warnRatio) return Warn;
            return Info;
        }
    }

    /// <summary>证据链：任何结论都必须挂上这些可回溯的事实。</summary>
    [Serializable]
    public class PerfEvidence
    {
        public string tool = "";       // 产出该证据的工具
        public string metric = "";     // 指标名
        public string value = "";      // 数值（字符串，便于直接引用）
        public string unit = "";       // 单位
        public string threshold = "";  // 对比的预算阈值
        public string source = "";     // 数据来源（采样帧范围 / 资源路径 / 文件:行）

        public PerfEvidence() { }

        public PerfEvidence(string tool, string metric, string value, string unit, string threshold, string source)
        {
            this.tool = tool; this.metric = metric; this.value = value;
            this.unit = unit; this.threshold = threshold; this.source = source;
        }

        public override string ToString()
        {
            return metric + "=" + value + (string.IsNullOrEmpty(unit) ? "" : unit)
                 + (string.IsNullOrEmpty(threshold) ? "" : " (预算 " + threshold + ")")
                 + (string.IsNullOrEmpty(source) ? "" : " @" + source);
        }
    }

    /// <summary>诊断结论。</summary>
    [Serializable]
    public class PerfFinding
    {
        public string id = "";
        public string category = "";      // 帧率 / 内存 / 渲染 / 物理 / 资源 / 代码
        public string severity = Severity.Info;
        public string title = "";
        public string detail = "";
        public string recommendation = "";
        public float confidence = 0.5f;
        public string jumpTo = "";        // 可跳转的目标：资源路径 / 对象层级路径 / 文件:行
        /// <summary>
        /// 对应的一键修复动作组（如 texture_readwrite）。
        /// 只有资源/场景审计类结论会填，规则类结论留空 —— 规划器据此判断能不能给出执行按钮。
        /// </summary>
        public string fixCode = "";
        public List<PerfEvidence> evidence = new List<PerfEvidence>();
    }

    /// <summary>单个标量指标。</summary>
    [Serializable]
    public class PerfMetric
    {
        public string name = "";
        public string unit = "";
        public double value;
        public double avg;
        public double min;
        public double max;
        public int samples;
        public string source = "";     // 数据来源
        public string budget = "";     // 预算阈值（原始数值）
        public string budgetUnit = "";
        public string severity = Severity.Info;
        // diff 用
        public double previous = double.NaN;
        public double delta;
    }

    /// <summary>单帧采样。</summary>
    [Serializable]
    public class FrameStat
    {
        public int frame;
        public double deltaMs;
        /// <summary>GC.GetTotalMemory 差值。弱口径：不含「当帧分配后立即回收」的部分，会低估分配量，仅用于交叉校验。</summary>
        public long managedAllocBytes;
        /// <summary>ProfilerRecorder「GC Allocated In Frame」，与 Profiler 窗口的 GC Alloc 列同源。0 表示该计数器不可用。</summary>
        public long allocInFrameBytes;
        public long tempAllocBytes;      // Profiler.GetTempAllocatorSize
        public int drawCalls;
        public int batches;
        public int setPassCalls;
        public long triangles;
        public long totalMemoryBytes;
    }

    /// <summary>Profiler 层级视图里的一条 marker 统计（实验性，见 ProfilerMarkerCollector）。</summary>
    [Serializable]
    public class MarkerStat
    {
        public string name = "";
        public string category = "";
        public double selfMs;
        public double totalMs;
        public long gcAllocBytes;
        public long calls;
        public int depth;
        public string thread = "Main Thread";
    }

    /// <summary>
    /// 逐帧数据的存储辅助。
    ///
    /// 刻意不引用 UnityEngine —— 抽取规则直接决定了长时间采集的数据完整性，
    /// 必须能被独立回归测试逐条钉死。
    /// </summary>
    public static class FrameStats
    {
        /// <summary>
        /// 把逐帧明细均匀抽取到 max 条。
        ///
        /// 为什么要抽：跑完一局游戏可能是几万帧，全量写进快照 JSON 会膨胀到几十 MB，
        /// 而其中绝大多数点在人眼看的曲线上根本分辨不出来。
        /// 注意只影响**落盘与展示**，所有统计指标始终基于全部原始帧。
        /// </summary>
        public static List<FrameStat> Downsample(List<FrameStat> frames, int max)
        {
            if (frames == null || max <= 0 || frames.Count <= max) return frames;

            var result = new List<FrameStat>(max);
            double step = (double)frames.Count / max;

            for (int i = 0; i < max; i++)
            {
                int index = (int)(i * step);
                if (index >= frames.Count) index = frames.Count - 1;
                result.Add(frames[index]);
            }
            return result;
        }
    }

    [Serializable]
    public class AssetIssue
    {
        /// <summary>
        /// 机器可读的问题类型（如 texture_readwrite）。
        /// issue 文案里带具体数值，不能用来做规则匹配，因此单独保留一个稳定标识供一键修复映射。
        /// </summary>
        public string code = "";
        public string path = "";
        public string assetType = "";
        public long diskBytes;
        public long estimatedMemoryBytes;
        public string issue = "";
        public string suggestion = "";
        public string severity = Severity.Info;
    }

    [Serializable]
    public class SceneIssue
    {
        /// <summary>机器可读的问题类型（如 scene_autosync_transforms），供一键修复映射。</summary>
        public string code = "";
        public string hierarchyPath = "";
        public string componentType = "";
        public string issue = "";
        public string detail = "";
        public string suggestion = "";
        public string severity = Severity.Info;
    }

    [Serializable]
    public class CodeIssue
    {
        public string file = "";
        public int line;

        /// <summary>
        /// 反模式标识，形如 "Update + linq" —— 前半段是所在方法，后半段才是反模式 id。
        ///
        /// **不要拿这个字符串直接和反模式 id 比较**，一定要过 BasePattern()。
        /// 曾经就是因为直接比较，导致 tag_compare / gc_collect 的自动修复从来没生效过：
        /// 拿 "Update + tag_compare" 去比 "tag_compare"，永远不等。
        /// </summary>
        public string pattern = "";
        public string snippet = "";
        public string suggestion = "";
        public string severity = Severity.Info;

        /// <summary>取出真正的反模式 id（去掉 "方法名 + " 前缀）。</summary>
        public static string BasePattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return "";

            int at = pattern.LastIndexOf(" + ", StringComparison.Ordinal);
            return at >= 0 ? pattern.Substring(at + 3) : pattern;
        }

        /// <summary>取出所在方法名（没有前缀时返回空串）。</summary>
        public static string MethodName(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return "";

            int at = pattern.LastIndexOf(" + ", StringComparison.Ordinal);
            return at > 0 ? pattern.Substring(0, at) : "";
        }
    }

    /// <summary>
    /// 统一数据模型：采集层产出它，规则引擎与 LLM 都消费同一份。
    /// 注意：JsonUtility 不支持 Dictionary 与顶层数组，故全部使用 List + 可序列化类。
    /// </summary>
    [Serializable]
    public class PerfSnapshot
    {
        // ---- 元信息 ----
        public string id = "";
        public string label = "";
        public string capturedUtc = "";
        public string unityVersion = "";
        public string platform = "";
        public string scenePath = "";
        public string buildTarget = "";

        // ---- 环境 ----
        public string deviceModel = "";
        public string graphicsDevice = "";
        public string processor = "";
        public int processorCount;
        public int systemMemoryMB;
        public int screenWidth;
        public int screenHeight;
        public int targetFrameRate;
        public int vSyncCount;
        public int qualityLevel;
        public string qualityName = "";
        public string colorSpace = "";
        public string renderPipeline = "";
        public string activeInputHandling = "";

        // ---- 物理 ----
        public float fixedDeltaTime;
        public string physicsSimulationMode = "";
        public bool physicsAutoSyncTransforms;
        public int physicsSolverIterations;
        public int physicsSolverVelocityIterations;

        // ---- 数据 ----
        public List<PerfMetric> metrics = new List<PerfMetric>();
        /// <summary>
        /// 本次采集的**原始**帧数。
        ///
        /// 长时间采集时 frames 只是均匀抽取后的子集（为控制文件体积），
        /// 所以「到底采了多少帧」必须看这个字段，不能看 frames.Count。
        /// 旧快照没有这个字段，读到 0 时回退到 frames.Count。
        /// </summary>
        public int capturedFrameCount;

        public List<FrameStat> frames = new List<FrameStat>();
        public List<MarkerStat> markers = new List<MarkerStat>();
        public List<AssetIssue> assetIssues = new List<AssetIssue>();
        public List<SceneIssue> sceneIssues = new List<SceneIssue>();
        public List<CodeIssue> codeIssues = new List<CodeIssue>();
        public List<PerfFinding> findings = new List<PerfFinding>();
        public List<string> notes = new List<string>();
        public List<string> capturedSources = new List<string>();

        // ---- 派生统计 ----

        public PerfMetric FindMetric(string name)
        {
            for (int i = 0; i < metrics.Count; i++)
                if (metrics[i].name == name) return metrics[i];
            return null;
        }

        public double MetricValue(string name, double def = double.NaN)
        {
            var m = FindMetric(name);
            return m == null ? def : m.value;
        }

        public double FrameTimeAvgMs()
        {
            if (frames.Count == 0) return double.NaN;
            double sum = 0;
            for (int i = 0; i < frames.Count; i++) sum += frames[i].deltaMs;
            return sum / frames.Count;
        }

        public double FrameTimePercentileMs(double percentile)
        {
            if (frames.Count == 0) return double.NaN;
            var arr = new List<double>(frames.Count);
            for (int i = 0; i < frames.Count; i++) arr.Add(frames[i].deltaMs);
            arr.Sort();
            int idx = (int)Math.Round((percentile / 100.0) * (arr.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= arr.Count) idx = arr.Count - 1;
            return arr[idx];
        }

        public double FrameTimeMaxMs()
        {
            double max = 0;
            for (int i = 0; i < frames.Count; i++) if (frames[i].deltaMs > max) max = frames[i].deltaMs;
            return max;
        }

        /// <summary>平均每帧托管分配（只统计正值，GC 事件记为负值被剔除）。这是降级口径，优先用 AvgRecorderAllocPerFrame。</summary>
        public double AvgManagedAllocBytesPerFrame()
        {
            if (frames.Count == 0) return double.NaN;
            double sum = 0; int n = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].managedAllocBytes > 0) { sum += frames[i].managedAllocBytes; n++; }
            }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>
        /// 是否拿到了 ProfilerRecorder 的 GC 分配口径。
        /// 这个口径与 Unity Profiler 窗口的 GC Alloc 列同源，比 GC.GetTotalMemory 差值可靠：
        /// 它统计当帧真实分配总量，而差值法看不见「分配后立即被回收」的部分。
        /// </summary>
        public bool HasRecorderGcAlloc()
        {
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].allocInFrameBytes > 0) return true;
            return false;
        }

        /// <summary>ProfilerRecorder 口径的平均每帧分配（字节）；该口径不可用时返回 NaN。</summary>
        public double AvgRecorderAllocPerFrame()
        {
            long sum = 0; int n = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].allocInFrameBytes <= 0) continue;
                sum += frames[i].allocInFrameBytes;
                n++;
            }
            return n == 0 ? double.NaN : (double)sum / n;
        }

        public int GcEventCount()
        {
            int n = 0;
            for (int i = 0; i < frames.Count; i++) if (frames[i].managedAllocBytes < 0) n++;
            return n;
        }

        /// <summary>帧耗时尖峰（超过 p95 的 1.2 倍）的帧号列表，最多返回 max 条。</summary>
        public List<int> SpikeFrames(int max = 10)
        {
            var result = new List<int>();
            if (frames.Count < 10) return result;
            double p95 = FrameTimePercentileMs(95);
            var spikes = new List<FrameStat>();
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].deltaMs > p95 * 1.2) spikes.Add(frames[i]);
            spikes.Sort((a, b) => b.deltaMs.CompareTo(a.deltaMs));
            for (int i = 0; i < spikes.Count && i < max; i++) result.Add(spikes[i].frame);
            return result;
        }

        public void AddMetric(PerfMetric m)
        {
            if (m == null) return;
            for (int i = 0; i < metrics.Count; i++)
            {
                if (metrics[i].name == m.name) { metrics[i] = m; return; }
            }
            metrics.Add(m);
        }

        public PerfMetric SetMetric(string name, string unit, double value, string source = "")
        {
            var m = new PerfMetric();
            m.name = name; m.unit = unit; m.value = value; m.source = source;
            AddMetric(m);
            return m;
        }

        public void AddNote(string note)
        {
            if (!string.IsNullOrEmpty(note) && !notes.Contains(note)) notes.Add(note);
        }

        public string Label()
        {
            return string.IsNullOrEmpty(label) ? id : label;
        }
    }
}

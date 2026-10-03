using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Collectors;
using PerfAgent.Utils;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// 面板里的一段「一帧一个值」序列（计数器或统计属性）。
    /// 与 Profiler 面板图表同源，整段窗口每一帧都有值。
    /// </summary>
    public class PanelSeries
    {
        public string category = "";
        public string name = "";
        public int firstFrame;
        public float[] values = new float[0];

        /// <summary>接口调用是否成功（与「有没有有效样本」是两回事）。</summary>
        public bool readable;

        /// <summary>不可读的原因。</summary>
        public string error = "";

        public int validCount;
        public double mean, p50, p95, max;

        public string Key { get { return category + "/" + name; } }

        /// <summary>取某帧的值；超出范围或无数据返回 0。</summary>
        public long ValueAt(int frame)
        {
            int i = frame - firstFrame;
            if (i < 0 || i >= values.Length) return 0L;
            float v = values[i];
            if (v <= 0f) return 0L;
            return v > long.MaxValue ? long.MaxValue : (long)Math.Round(v);
        }

        /// <summary>只统计有效样本（面板里没记录的帧值是 0，不能算进去）。</summary>
        public void ComputeStats()
        {
            var valid = new List<double>(values == null ? 0 : values.Length);
            double sum = 0;
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    float v = values[i];
                    if (v <= 0f) continue;
                    valid.Add(v);
                    sum += v;
                    if (v > max) max = v;
                }
            }

            validCount = valid.Count;
            if (validCount == 0) return;

            mean = sum / validCount;
            valid.Sort();
            p50 = Percentile(valid, 50);
            p95 = Percentile(valid, 95);
        }

        static double Percentile(List<double> sorted, int percentile)
        {
            int idx = (int)Math.Round((percentile / 100.0) * (sorted.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }
    }

    /// <summary>
    /// 一次「读 Profiler 面板」的结果 —— 取代原来的逐帧自采样。
    ///
    /// 为什么改成读面板：
    ///   1. 自采样本身就在给编辑器加开销，而它要测的正是「每帧开销」，等于自己污染自己的测量；
    ///   2. 自采样要维护上万帧的内存缓冲（跟随采集原上限 100 万帧）；
    ///   3. 面板本来就把这些都记录了，而且帧耗时是引擎自己测的，不是 Stopwatch 估的。
    /// </summary>
    public class PanelCaptureData
    {
        public bool available;
        public string unavailableReason = "";

        public int firstFrame = -1, lastFrame = -1, frameCount;

        /// <summary>抽样帧（从面板窗口里均匀抽取，最多 <see cref="ProfilerPanel.MaxSamples"/> 帧）。</summary>
        public readonly List<FrameStat> samples = new List<FrameStat>();

        /// <summary>抽样步长（1 = 全取）。</summary>
        public int sampleStep = 1;

        // 整段窗口的序列（非抽样）
        public PanelSeries gcAlloc, drawCalls, setPass, triangles, batches, textureMemory, totalUsed;

        /// <summary>帧耗时的实际来源说明（写进快照，便于核验）。</summary>
        public string frameTimeSource = "";

        /// <summary>被丢弃的启动拖动帧数（0 = 没丢）。</summary>
        public int warmupFramesDropped;

        /// <summary>丢弃启动拖动的目标秒数（0 = 未开）。</summary>
        public double warmupSeconds;

        public readonly List<string> notes = new List<string>();
    }

    /// <summary>
    /// Profiler 面板数据源：只读面板已记录的内容，不做任何逐帧采样。
    ///
    /// 数据链路（全部走公开/内部接口的反射，见 ProfilerApi）：
    ///   帧范围      → ProfilerDriver.firstFrameIndex / lastFrameIndex
    ///   整段序列    → GetCounterValuesBatchByCategory（与面板图表同源，每帧都有值）
    ///   单帧耗时    → HierarchyFrameDataView 里 PlayerLoop 行的总耗时（**不含编辑器开销**，
    ///                 这点比原来的 Stopwatch 墙钟测量准得多），取不到时退回 frameTimeMs
    /// </summary>
    public static class ProfilerPanel
    {
        /// <summary>单次分析最多抽多少帧读「单帧明细」（帧耗时/热点）。</summary>
        public const int MaxSamples = 300;

        /// <summary>单次分析最多覆盖多少帧（面板默认历史也就两千帧）。</summary>
        public const int MaxWindowFrames = 5000;

        public static bool IsAvailable { get { return ProfilerApi.CanReadFrames; } }
        public static int FirstRecordedFrame { get { return ProfilerApi.FirstFrameIndex; } }
        public static int LastRecordedFrame { get { return ProfilerApi.LastFrameIndex; } }

        public static int RecordedFrameCount
        {
            get
            {
                int a = FirstRecordedFrame, b = LastRecordedFrame;
                return (a < 0 || b < a) ? 0 : b - a + 1;
            }
        }

        /// <summary>
        /// 读一段面板窗口。firstFrame &lt; 0 表示「从面板最早的一帧开始」，
        /// lastFrame &lt; 0 表示「到最新一帧为止」。
        /// </summary>
        public static PanelCaptureData Read(int firstFrame, int lastFrame, int maxSamples = MaxSamples, double skipSeconds = 0)
        {
            var data = new PanelCaptureData();

            if (!ProfilerApi.CanReadFrames)
            {
                data.unavailableReason = "ProfilerDriver / HierarchyFrameDataView 不可用";
                return data;
            }

            int panelFirst = ProfilerApi.FirstFrameIndex;
            int panelLast = ProfilerApi.LastFrameIndex;
            if (panelLast < panelFirst || panelFirst < 0)
            {
                data.unavailableReason = "Profiler 面板里没有已记录的帧（先让它录一段）";
                return data;
            }

            if (firstFrame < 0) firstFrame = panelFirst;
            if (lastFrame < 0) lastFrame = panelLast;
            if (firstFrame < panelFirst) firstFrame = panelFirst;
            if (lastFrame > panelLast) lastFrame = panelLast;
            if (lastFrame < firstFrame)
            {
                data.unavailableReason = "这段帧已经被挤出面板历史（当前可用 " + panelFirst + "~" + panelLast + "）";
                return data;
            }

            if (lastFrame - firstFrame + 1 > MaxWindowFrames)
            {
                data.notes.Add("面板里这段有 " + (lastFrame - firstFrame + 1) + " 帧，超过单次上限，只分析最近 "
                               + MaxWindowFrames + " 帧。");
                firstFrame = lastFrame - MaxWindowFrames + 1;
            }

            data.available = true;
            data.firstFrame = firstFrame;
            data.lastFrame = lastFrame;
            data.frameCount = lastFrame - firstFrame + 1;

            // 整段窗口的序列：每一帧都有值，不是抽样
            ReadAllSeries(data);

            ReadSamples(data, maxSamples);

            // 丢掉进入 Play 的启动拖动（域重载 / 首次 Shader 编译 / 资源初始化）
            if (skipSeconds > 0) TrimWarmup(data, skipSeconds);

            if (data.samples.Count == 0)
                data.notes.Add("没能从面板读到任何单帧明细（帧耗时取不到）：本次只能给整段序列的统计，不给帧耗时结论。");
            else if (!string.IsNullOrEmpty(data.frameTimeSource))
                data.notes.Add("帧耗时来源：" + data.frameTimeSource + "（其中 " + data.samples.Count + "/" + data.frameCount + " 帧参与统计）。");

            if (data.gcAlloc != null && !data.gcAlloc.readable)
                data.notes.Add("面板的 GC 分配序列读不到（" + data.gcAlloc.error + "），本次不给每帧分配结论。");

            return data;
        }

        static void ReadAllSeries(PanelCaptureData data)
        {
            data.gcAlloc = ReadSeries("Memory", "GC Allocated In Frame", data.firstFrame, data.frameCount);
            data.drawCalls = ReadSeries("Render", "Draw Calls Count", data.firstFrame, data.frameCount);
            data.setPass = ReadSeries("Render", "SetPass Calls Count", data.firstFrame, data.frameCount);
            data.triangles = ReadSeries("Render", "Triangles Count", data.firstFrame, data.frameCount);
            data.batches = ReadSeries("Render", "Batches Count", data.firstFrame, data.frameCount);
            data.textureMemory = ReadSeries("Memory", "Texture Memory", data.firstFrame, data.frameCount);
            data.totalUsed = ReadSeries("Memory", "Total Used Memory", data.firstFrame, data.frameCount);
        }

        /// <summary>
        /// 丢掉窗口开头的启动拖动。用抽样帧的中位耗时把「秒」换算成「帧」，然后裁窗口重读序列。
        /// </summary>
        static void TrimWarmup(PanelCaptureData data, double skipSeconds)
        {
            if (data.samples.Count == 0) return;

            var times = new List<double>(data.samples.Count);
            for (int i = 0; i < data.samples.Count; i++) times.Add(data.samples[i].deltaMs);
            times.Sort();
            double median = times[times.Count / 2];
            if (median <= 0) return;

            int drop = (int)Math.Round(skipSeconds * 1000.0 / median);
            if (drop > data.frameCount - 10) drop = Math.Max(0, data.frameCount - 10);
            if (drop <= 0) return;

            data.firstFrame += drop;
            data.frameCount -= drop;
            data.warmupFramesDropped = drop;
            data.warmupSeconds = skipSeconds;

            // 序列按新窗口重读（一次原生批量调用，很便宜）
            ReadAllSeries(data);
            data.samples.RemoveAll(delegate (FrameStat f) { return f.frame < data.firstFrame; });
        }

        static PanelSeries ReadSeries(string category, string name, int firstFrame, int count)
        {
            var s = new PanelSeries();
            s.category = category;
            s.name = name;
            s.firstFrame = firstFrame;
            s.values = new float[count];
            s.readable = ProfilerApi.ReadCounterSeries(category, name, firstFrame, s.values);
            if (!s.readable) s.error = "接口调用失败";
            s.ComputeStats();
            return s;
        }

        /// <summary>均匀抽样若干帧，逐帧读「游戏帧耗时」（PlayerLoop 行）。</summary>
        static void ReadSamples(PanelCaptureData data, int maxSamples)
        {
            int total = data.frameCount;
            int wanted = Math.Min(Math.Max(1, maxSamples), total);
            data.sampleStep = wanted <= 0 ? 1 : Math.Max(1, total / wanted);

            int lastAdded = -1;
            for (int i = 0; i < wanted; i++)
            {
                // 均匀取样；一定包含最后一帧（「刚才那一下」通常就在末尾）
                int idx = total <= wanted
                    ? data.firstFrame + i
                    : data.firstFrame + (int)((long)i * (total - 1) / Math.Max(1, wanted - 1));
                if (idx == lastAdded) continue;

                var view = ProfilerApi.GetHierarchyView(idx, 0);
                if (view == null) continue;

                bool valid;
                if (Reflect.TryGetBool(view, "valid", out valid) && !valid) continue;

                double ms = ReadFrameTimeMs(view, data);
                if (ms <= 0) continue;

                lastAdded = idx;
                var stat = new FrameStat();
                stat.frame = idx;
                stat.deltaMs = ms;
                stat.allocInFrameBytes = data.gcAlloc == null ? 0L : data.gcAlloc.ValueAt(idx);
                stat.drawCalls = (int)(data.drawCalls == null ? 0L : data.drawCalls.ValueAt(idx));
                stat.setPassCalls = (int)(data.setPass == null ? 0L : data.setPass.ValueAt(idx));
                stat.triangles = data.triangles == null ? 0L : data.triangles.ValueAt(idx);
                stat.batches = (int)(data.batches == null ? 0L : data.batches.ValueAt(idx));
                stat.tempAllocBytes = 0L;
                stat.totalMemoryBytes = data.totalUsed == null ? 0L : data.totalUsed.ValueAt(idx);
                data.samples.Add(stat);
            }
        }

        /// <summary>
        /// 单帧耗时：优先读 PlayerLoop 行的总耗时 —— 那是**游戏**这一帧花了多久，
        /// 不含编辑器的 Inspector / GUI / SceneView；面板的 frameTimeMs 是整帧（含编辑器），
        /// 只在找不到 PlayerLoop 行时退回使用，并在 source 里写明。
        /// </summary>
        static double ReadFrameTimeMs(object view, PanelCaptureData data)
        {
            int columns = ProfilerApi.GetColumnCount(view);
            if (columns < 2) columns = 8;

            int root = ProfilerApi.GetRootItemId(view);
            var kids = ProfilerApi.GetChildren(view, root);
            int loopId = -1;
            for (int i = 0; i < kids.Count; i++)
            {
                if (ProfilerApi.GetItemName(view, kids[i]) == "PlayerLoop") { loopId = kids[i]; break; }
            }

            if (loopId >= 0)
            {
                // 列语义用「PlayerLoop 行 + 它前几个子项」一起识别：单行反推不可靠
                var rows = new List<string[]>();
                rows.Add(ReadRow(view, loopId, columns));
                var loopKids = ProfilerApi.GetChildren(view, loopId);
                for (int i = 0; i < loopKids.Count && i < 8; i++) rows.Add(ReadRow(view, loopKids[i], columns));

                var layout = ProfilerColumnLayout.Detect(rows);
                double total = MaxMs(rows[0], layout);
                if (total > 0)
                {
                    if (string.IsNullOrEmpty(data.frameTimeSource))
                        data.frameTimeSource = "面板 HierarchyFrameDataView 的 PlayerLoop 总耗时（不含编辑器开销）";
                    return total;
                }
            }

            var raw = Reflect.Get(view, "frameTimeMs");
            if (raw != null)
            {
                try
                {
                    double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (v > 0)
                    {
                        if (string.IsNullOrEmpty(data.frameTimeSource))
                            data.frameTimeSource = "面板 FrameDataView.frameTimeMs（含编辑器开销，没找到 PlayerLoop 行）";
                        return v;
                    }
                }
                catch { }
            }
            return -1;
        }

        static string[] ReadRow(object view, int id, int columns)
        {
            var row = new string[columns];
            for (int c = 0; c < columns; c++) row[c] = ProfilerApi.GetItemColumn(view, id, c);
            return row;
        }

        /// <summary>一行里所有耗时列取最大值 —— 总耗时列一定是最大的那个（Self ≤ Total）。</summary>
        static double MaxMs(string[] row, ProfilerColumnLayout layout)
        {
            double max = 0;
            for (int i = 0; i < layout.msColumns.Count; i++)
            {
                int col = layout.msColumns[i];
                if (col < 0 || col >= row.Length) continue;
                double v = ProfilerColumnLayout.ParseNumber(row[col]);
                if (!double.IsNaN(v) && v > max) max = v;
            }
            return max;
        }
    }
}

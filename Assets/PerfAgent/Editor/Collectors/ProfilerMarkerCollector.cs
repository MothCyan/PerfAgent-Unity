using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Collectors
{
    /// <summary>
    /// 从 Profiler 层级视图读取 marker 排行（Self Time / GC Alloc / Calls）。
    ///
    /// 【实验性】HierarchyFrameDataView 的列语义在不同 Unity 版本间有差异，
    /// 因此这里不硬编码列索引，而是：
    ///   1. 反射取到视图对象与其子项；
    ///   2. 把每行所有列都取成字符串；
    ///   3. 按「内容形态」自动分类：12.3ms -> 时间；1.2KB/512B -> 内存；1,234 -> 调用次数。
    /// 这样即使列顺序或名字变了，也只会退化成「数据缺失」，不会产生错误数据。
    /// 用 Tools/PerfAgent/API 探针 可以看到本机实际可用的成员清单。
    /// </summary>
    public class ProfilerMarkerCollector : IPerfCollector
    {
        public string Name { get { return "Marker 排行"; } }
        public string ToolName { get { return "profile_markers"; } }
        public string Description { get { return "Profiler 层级视图 Top marker（自身耗时 / GC 分配 / 调用次数），用于定位耗时集中在哪个函数。"; } }

        const int MaxItems = 400;

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            if (!ProfilerApi.CanReadFrames)
            {
                s.AddNote("Profiler 帧数据 API 不可用（ProfilerDriver / HierarchyFrameDataView 缺失），已跳过 marker 采集。");
                return;
            }

            int last = ProfilerApi.LastFrameIndex;
            if (last < 0)
            {
                s.AddNote("Profiler 当前没有可用帧。抓帧前请确保 Profiler 处于记录状态。");
                return;
            }

            var collected = new List<MarkerStat>();
            int read = 0;
            // 逆序读最近若干帧，任一帧成功即可
            for (int frame = last; frame >= 0 && read < 3; frame--)
            {
                var list = ReadFrame(frame, ctx.topN);
                if (list != null && list.Count > 0)
                {
                    collected.AddRange(list);
                    read++;
                }
            }

            if (collected.Count == 0)
            {
                s.AddNote("未能从 Profiler 层级视图解析出 marker（该版本 API 形态可能不同，请运行 API 探针）。");
                return;
            }

            collected.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            s.markers = collected;

            var top = collected[0];
            if (top.selfMs > 0)
                s.SetMetric("最耗时 Marker", "ms", top.selfMs, "HierarchyFrameDataView: " + top.name);
        }

        List<MarkerStat> ReadFrame(int frameIndex, int topN)
        {
            var view = ProfilerApi.GetHierarchyView(frameIndex, 0);
            if (view == null) return null;

            bool valid;
            if (Reflect.TryGetBool(view, "valid", out valid) && !valid) return null;

            int columns = ProfilerApi.GetColumnCount(view);
            if (columns < 2) columns = 8;

            var result = new List<MarkerStat>();
            var queue = new Queue<KeyValuePair<int, int>>();
            queue.Enqueue(new KeyValuePair<int, int>(ProfilerApi.GetRootItemId(view), 0));
            int visited = 0;

            while (queue.Count > 0 && visited < MaxItems)
            {
                var pair = queue.Dequeue();
                int id = pair.Key;
                int depth = pair.Value;
                visited++;

                var children = ProfilerApi.GetChildren(view, id);
                for (int i = 0; i < children.Count; i++)
                    queue.Enqueue(new KeyValuePair<int, int>(children[i], depth + 1));

                if (depth == 0) continue; // 跳过根节点

                var stat = ParseRow(view, id, columns);
                if (stat == null) continue;
                stat.depth = depth;
                if (string.IsNullOrEmpty(stat.name) || stat.name == "(unknown)") continue;
                result.Add(stat);
            }

            result.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            if (topN > 0 && result.Count > topN) result.RemoveRange(topN, result.Count - topN);
            return result;
        }

        static MarkerStat ParseRow(object view, int id, int columns)
        {
            var stat = new MarkerStat();
            stat.name = ProfilerApi.GetItemName(view, id);

            double timeA = -1, timeB = -1;
            long memBytes = 0;
            long calls = 0;
            bool any = false;

            for (int col = 0; col < columns; col++)
            {
                string raw = ProfilerApi.GetItemColumn(view, id, col);
                if (string.IsNullOrEmpty(raw)) continue;
                string cell = raw.Trim();

                double ms;
                if (TryParseMs(cell, out ms))
                {
                    any = true;
                    if (timeA < 0) timeA = ms;
                    else if (timeB < 0) timeB = ms;
                    continue;
                }

                long bytes;
                if (TryParseBytes(cell, out bytes))
                {
                    any = true;
                    if (bytes > memBytes) memBytes = bytes;
                    continue;
                }

                long n;
                if (TryParseCount(cell, out n))
                {
                    any = true;
                    if (n > calls) calls = n;
                }
            }

            if (!any) return null;

            // 同一行里出现两个 ms 值时：较大的通常是 Total，较小的是 Self（Self <= Total 恒成立）
            if (timeA >= 0 && timeB >= 0)
            {
                stat.totalMs = Math.Max(timeA, timeB);
                stat.selfMs = Math.Min(timeA, timeB);
            }
            else if (timeA >= 0)
            {
                stat.selfMs = timeA;
                stat.totalMs = timeA;
            }

            stat.gcAllocBytes = memBytes;
            stat.calls = calls;
            return stat;
        }

        static bool TryParseMs(string cell, out double ms)
        {
            ms = 0;
            if (cell.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                return double.TryParse(cell.Substring(0, cell.Length - 2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
            if (cell.EndsWith("s", StringComparison.OrdinalIgnoreCase) && cell.Length > 1
                && !cell.EndsWith("us", StringComparison.OrdinalIgnoreCase))
            {
                double sec;
                if (double.TryParse(cell.Substring(0, cell.Length - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                {
                    ms = sec * 1000.0;
                    return true;
                }
            }
            return false;
        }

        static bool TryParseBytes(string cell, out long bytes)
        {
            bytes = 0;
            string upper = cell.ToUpperInvariant();
            double factor = 0;
            string numberPart = null;

            if (upper.EndsWith("KB")) { factor = 1024; numberPart = cell.Substring(0, cell.Length - 2); }
            else if (upper.EndsWith("MB")) { factor = 1024 * 1024; numberPart = cell.Substring(0, cell.Length - 2); }
            else if (upper.EndsWith("GB")) { factor = 1024L * 1024 * 1024; numberPart = cell.Substring(0, cell.Length - 2); }
            else if (upper.EndsWith("B") && upper.Length > 1 && !upper.EndsWith("KB") && !upper.EndsWith("MB"))
            {
                // 形如 "512B"，但 "1.2B" 也可能是别的单位，保守处理
                numberPart = cell.Substring(0, cell.Length - 1);
                factor = 1;
            }

            if (numberPart == null || factor <= 0) return false;
            double v;
            if (!double.TryParse(numberPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            bytes = (long)(v * factor);
            return true;
        }

        static bool TryParseCount(string cell, out long count)
        {
            count = 0;
            // 形如 "1,234" 或 "56"
            string cleaned = cell.Replace(",", "").Replace(" ", "");
            if (cleaned.Length == 0) return false;
            for (int i = 0; i < cleaned.Length; i++)
                if (!char.IsDigit(cleaned[i])) return false;
            return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
        }
    }
}

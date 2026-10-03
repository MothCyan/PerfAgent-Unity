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

        /// <summary>最近一次读取识别出的列语义（供提示与探针使用）。</summary>
        internal static string LayoutText = "";

        /// <summary>最近一次统计的范围（PlayerLoop 子树 / 全部）。</summary>
        internal static string ScopeText = "";

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
                var why = ProfilerApi.LastHierarchyError;
                if (string.IsNullOrEmpty(why) && !string.IsNullOrEmpty(LayoutText)) why = "列语义识别结果：" + LayoutText;
                s.AddNote("未能从 Profiler 层级视图解析出 marker"
                          + (string.IsNullOrEmpty(why)
                              ? "（视图可用，但各列内容都没能识别出耗时/分配/调用次数）"
                              : "：" + why)
                          + "。可用 Tools/PerfAgent/API 探针 查看本机 API 形态。");
                return;
            }

            // 有行但全无数据（selfMs / gcAllocBytes 都是 0）说明列语义没对上。
            // 这种排行拿出去只会误导 —— 宁可写一句「没解析出来」，也不输出一份假排行。
            bool anyUseful = false;
            for (int i = 0; i < collected.Count; i++)
            {
                if (collected[i].selfMs > 0 || collected[i].gcAllocBytes > 0 || collected[i].calls > 1) { anyUseful = true; break; }
            }
            if (!anyUseful)
            {
                s.AddNote("Profiler 层级视图能打开，但 " + collected.Count + " 行里没有任何一行带耗时或分配数据"
                          + (string.IsNullOrEmpty(LayoutText) ? "" : "（列语义识别结果：" + LayoutText + "）")
                          + "，已丢弃这份排行而不是展示空数据。用 Tools/PerfAgent/API 探针 可看到每列的实际内容。");
                return;
            }

            collected.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            s.markers = collected;

            if (!string.IsNullOrEmpty(ScopeText) && ScopeText.StartsWith("PlayerLoop", StringComparison.Ordinal))
                s.AddNote("Marker 排行只统计 " + ScopeText + "。"
                          + "编辑器的 Inspector / GUI / SceneView 开销不计入，否则会把编辑器自己的耗时算成项目热点。");

            var top = collected[0];
            if (top.selfMs > 0)
                s.SetMetric("最耗时 Marker", "ms", top.selfMs,
                    "HierarchyFrameDataView: " + top.name + "（" + ScopeText + "）");
        }

        List<MarkerStat> ReadFrame(int frameIndex, int topN)
        {
            var view = ProfilerApi.GetHierarchyView(frameIndex, 0);
            if (view == null) return null;

            try
            {
                return ReadFrameFromView(view, topN);
            }
            finally
            {
                // 原生视图必须释放，否则这一帧的帧数据会被钉住（读多了会把内存吃穿）
                ProfilerApi.ReleaseView(view);
            }
        }

        List<MarkerStat> ReadFrameFromView(object view, int topN)
        {
            bool valid;
            if (Reflect.TryGetBool(view, "valid", out valid) && !valid) return null;

            int columns = ProfilerApi.GetColumnCount(view);
            if (columns < 2) columns = 8;

            // 编辑器的主线程层级里，编辑器开销（EditorLoop）与游戏逻辑（PlayerLoop）是并列的。
            // 有 PlayerLoop 就只统计它 —— 这份排行是给「项目热点」用的，
            // 不该把 Inspector / GUI / SceneView 的耗时算成项目的热点。
            int startId = ProfilerApi.GetRootItemId(view);
            var roots = ProfilerApi.GetChildren(view, startId);
            string scope = "全部（未找到 PlayerLoop 子树）";
            for (int i = 0; i < roots.Count; i++)
            {
                if (ProfilerApi.GetItemName(view, roots[i]) == "PlayerLoop")
                {
                    startId = roots[i];
                    scope = "PlayerLoop 子树（已排除编辑器开销）";
                    break;
                }
            }

            // 1) 先把行读成文本（列语义必须从多行数据里统计，单行反推会认错列）
            var ids = new List<int>();
            var depths = new List<int>();
            var names = new List<string>();
            var cells = new List<string[]>();

            var queue = new Queue<KeyValuePair<int, int>>();
            queue.Enqueue(new KeyValuePair<int, int>(startId, 0));
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

                if (depth == 0) continue; // 跳过根节点（PlayerLoop/整帧那一行）

                var row = new string[columns];
                for (int c = 0; c < columns; c++) row[c] = ProfilerApi.GetItemColumn(view, id, c);

                ids.Add(id);
                depths.Add(depth);
                names.Add(ProfilerApi.GetItemName(view, id));
                cells.Add(row);
            }

            if (ids.Count == 0) return null;

            // 2) 识别列语义 → 按列类别取值
            var layout = ProfilerColumnLayout.Detect(cells);
            LayoutText = layout.Describe();
            ScopeText = scope;

            if (!layout.IsUsable) return null;

            var result = new List<MarkerStat>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name) || name == "(unknown)") continue;

                var stat = new MarkerStat();
                stat.name = name;
                stat.depth = depths[i];

                // 耗时：可能有两列（Total / Self，实测都是裸小数）。Self ≤ Total 恒成立，
                // 取小者作为自身耗时 —— 这样与该版本的列顺序无关，也不会把父节点的总耗时当自身耗时。
                double self = double.NaN, total = double.NaN;
                for (int k = 0; k < layout.msColumns.Count; k++)
                {
                    double v = ProfilerColumnLayout.ParseNumber(cells[i][layout.msColumns[k]]);
                    if (double.IsNaN(v)) continue;
                    if (double.IsNaN(self) || v < self) self = v;
                    if (double.IsNaN(total) || v > total) total = v;
                }
                stat.selfMs = double.IsNaN(self) ? 0 : self;
                stat.totalMs = double.IsNaN(total) ? stat.selfMs : total;

                if (layout.gcAllocColumn >= 0)
                    stat.gcAllocBytes = ProfilerColumnLayout.ParseBytes(cells[i][layout.gcAllocColumn]);
                if (layout.callsColumn >= 0)
                    stat.calls = ProfilerColumnLayout.ParseCount(cells[i][layout.callsColumn]);

                result.Add(stat);
            }

            result.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            if (topN > 0 && result.Count > topN) result.RemoveRange(topN, result.Count - topN);
            return result;
        }
    }
}

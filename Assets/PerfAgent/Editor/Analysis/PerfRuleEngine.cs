using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 确定性规则引擎。
    ///
    /// 这是整个工具的价值底座：它不依赖任何 LLM，独立产出带证据链的结论。
    /// LLM 只负责在它的输出之上做编排与解释。断网、无 Key 时工具依然可用。
    /// </summary>
    public class PerfRuleEngine
    {
        public List<PerfFinding> Evaluate(PerfSnapshot s, PerfBudget b)
        {
            var findings = new List<PerfFinding>();
            if (s == null) return findings;

            if (b == null) b = new PerfBudget();

            EvaluateFrameTime(findings, s, b);
            EvaluateAllocations(findings, s, b);
            EvaluateMemory(findings, s, b);
            EvaluateRendering(findings, s, b);
            EvaluateMarkers(findings, s);
            EvaluateAssets(findings, s);
            EvaluateScene(findings, s);
            EvaluatePhysics(findings, s);
            EvaluateCode(findings, s);
            EvaluateConfiguration(findings, s);

            findings.Sort((x, y) =>
            {
                int bySeverity = Severity.Rank(y.severity).CompareTo(Severity.Rank(x.severity));
                if (bySeverity != 0) return bySeverity;
                return y.confidence.CompareTo(x.confidence);
            });

            s.findings = findings;
            return findings;
        }

        // =====================================================================
        // 帧耗时
        // =====================================================================

        static void EvaluateFrameTime(List<PerfFinding> outList, PerfSnapshot s, PerfBudget b)
        {
            var avgM = s.FindMetric("帧耗时均值");
            if (avgM == null) return;

            double budgetMs = b.FrameBudgetMs();
            double p95 = s.FrameTimePercentileMs(95);
            double max = s.FrameTimeMaxMs();
            string src = string.Format(CultureInfo.InvariantCulture, "FrameCapture/{0} 帧", s.frames.Count);

            if (budgetMs > 0 && p95 > budgetMs)
            {
                var f = New("frame_time_over", "帧率", Severity.Error,
                    string.Format(CultureInfo.InvariantCulture, "帧耗时 P95 超出预算（{0:0.##} ms > {1:0.##} ms）", p95, budgetMs),
                    "超过 5% 的帧达不到目标帧率，玩家可感知卡顿。",
                    "先看 Top Marker 定位耗时集中的模块，再看渲染统计判断是否为提交批次瓶颈。", 0.9f);
                Ev(f, "frame_capture", "帧耗时 P95", Fmt(p95), "ms", Fmt(budgetMs), src);
                Ev(f, "frame_capture", "帧耗时均值", Fmt(avgM.value), "ms", Fmt(budgetMs), src);
                Ev(f, "frame_capture", "帧耗时峰值", Fmt(max), "ms", "", src);
                outList.Add(f);
            }
            else if (budgetMs > 0 && avgM.value > budgetMs * b.warnRatio)
            {
                var f = New("frame_time_near_budget", "帧率", Severity.Warn,
                    string.Format(CultureInfo.InvariantCulture, "帧耗时均值已接近预算（{0:0.##} ms，预算 {1:0.##} ms）", avgM.value, budgetMs),
                    "余量不足，一旦出现峰值就会掉帧。",
                    "为高开销逻辑预留更多预算，或降低渲染/物理开销。", 0.7f);
                Ev(f, "frame_capture", "帧耗时均值", Fmt(avgM.value), "ms", Fmt(budgetMs * b.warnRatio), src);
                outList.Add(f);
            }

            // 抖动
            if (s.frames.Count >= 30)
            {
                var spikes = s.SpikeFrames(20);
                if (spikes.Count >= 3 && p95 > 0 && max > p95 * 1.5)
                {
                    var f = New("frame_time_jitter", "帧率", Severity.Warn,
                        string.Format(CultureInfo.InvariantCulture, "帧耗时抖动明显：峰值 {0:0.##} ms 是 P95（{1:0.##} ms）的 {2:0.#} 倍", max, p95, max / p95),
                        "稳态帧率尚可但存在周期性卡顿尖峰，通常是 GC、资源加载或物理集中计算造成。",
                        "对比尖峰帧与平均帧的指标差异（get_frames 工具），定位尖峰来源。", 0.75f);
                    Ev(f, "frame_capture", "帧耗时峰值", Fmt(max), "ms", "P95×1.5 = " + Fmt(p95 * 1.5), src);
                    Ev(f, "frame_capture", "帧耗时 P95", Fmt(p95), "ms", "", src);
                    outList.Add(f);

                    // 尖峰归因
                    var attribution = AttributeSpikes(s, spikes);
                    if (attribution != null) outList.Add(attribution);
                }
            }
        }

        /// <summary>尖峰归因：把尖峰帧的渲染/内存/分配指标与平均帧对比。</summary>
        static PerfFinding AttributeSpikes(PerfSnapshot s, List<int> spikeFrames)
        {
            if (spikeFrames.Count == 0) return null;
            string frameList = string.Join(",", spikeFrames.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());

            double spikeAlloc = 0, baseAlloc = 0;
            int spikeDc = 0, baseDc = 0;
            int ns = 0, nb = 0;
            for (int i = 0; i < s.frames.Count; i++)
            {
                var fr = s.frames[i];
                bool isSpike = spikeFrames.Contains(fr.frame);
                if (isSpike)
                {
                    spikeAlloc += Math.Max(0, fr.managedAllocBytes);
                    spikeDc += fr.drawCalls;
                    ns++;
                }
                else
                {
                    baseAlloc += Math.Max(0, fr.managedAllocBytes);
                    baseDc += fr.drawCalls;
                    nb++;
                }
            }
            if (ns == 0 || nb == 0) return null;

            spikeAlloc /= ns; baseAlloc /= nb;
            double spikeDcAvg = spikeDc / (double)ns, baseDcAvg = baseDc / (double)nb;

            var f = New("spike_attribution", "帧率", Severity.Info,
                "尖峰帧归因",
                string.Format(CultureInfo.InvariantCulture,
                    "尖峰帧（{0}）相对普通帧：每帧分配 {1:0} B vs {2:0} B，Draw Call {3:0.#} vs {4:0.#}。",
                    frameList, spikeAlloc, baseAlloc, spikeDcAvg, baseDcAvg),
                spikeAlloc > baseAlloc * 2 && spikeAlloc > 1024
                    ? "分配量差异显著，尖峰很可能由 GC / 临时分配引起，优先排查尖峰帧上的字符串、容器与协程分配。"
                    : "分配量差异不明显，更可能是同步加载、物理集中计算或 Shader 变体首次编译造成。",
                0.6f);
            Ev(f, "frame_capture", "尖峰帧平均分配", Fmt(spikeAlloc), "B", "普通帧 " + Fmt(baseAlloc) + " B", "帧 " + frameList);
            Ev(f, "frame_capture", "尖峰帧平均 Draw Call", Fmt(spikeDcAvg), "次", "普通帧 " + Fmt(baseDcAvg) + " 次", "帧 " + frameList);
            return f;
        }

        // =====================================================================
        // 分配与 GC
        // =====================================================================

        static void EvaluateAllocations(List<PerfFinding> outList, PerfSnapshot s, PerfBudget b)
        {
            var rawAlloc = s.FindMetric("每帧托管分配");
            var baseline = s.FindMetric("编辑器开销基线");
            var alloc = s.FindMetric("项目每帧分配");

            // 只对「扣除编辑器开销后的估算值」下结论。
            // 拿含编辑器开销的实测值去比播放器预算，会把空工程报成严重问题 ——
            // 那是工具制造的假问题，不是项目的问题。没有归因结果时宁可不报。
            if (alloc != null && !double.IsNaN(alloc.value))
            {
                // 基线与 Play 模式的实际开销不完全一致，留一条噪声带；没量到基线就不信任残差
                double noise = baseline == null ? double.MaxValue : Math.Max(4096.0, baseline.value * 0.25);

                if (alloc.value > b.maxManagedAllocBytesPerFrame && alloc.value > noise)
                {
                    var f = New("gc_alloc_per_frame", "内存", Severity.Error,
                        string.Format(CultureInfo.InvariantCulture, "项目每帧托管分配约 {0:0} B，超出预算 {1} B",
                            alloc.value, b.maxManagedAllocBytesPerFrame),
                        string.Format(CultureInfo.InvariantCulture,
                            "已扣除编辑器自身开销（实测 {0:0} B/帧 − 基线 {1:0} B/帧）。"
                            + "稳态每帧分配会导致 GC 周期性触发，表现为规律性卡顿尖峰。",
                            rawAlloc == null ? 0 : rawAlloc.value,
                            baseline == null ? 0 : baseline.value),
                        "扫描脚本反模式列表（get_code_issues），优先处理每帧 new 容器/字符串/LINQ 的写法。", 0.8f);
                    Ev(f, "frame_capture", "项目每帧分配", Fmt(alloc.value), "B",
                        b.maxManagedAllocBytesPerFrame.ToString(CultureInfo.InvariantCulture), alloc.source);
                    if (rawAlloc != null)
                        Ev(f, "frame_capture", "每帧托管分配（含编辑器开销）", Fmt(rawAlloc.value), "B", "", rawAlloc.source);
                    if (baseline != null)
                        Ev(f, "frame_capture", "编辑器开销基线", Fmt(baseline.value), "B", "", baseline.source);
                    outList.Add(f);
                }
                else if (alloc.value > 0 && b.maxManagedAllocBytesPerFrame == 0 && alloc.value > noise)
                {
                    var f = New("gc_alloc_nonzero", "内存", Severity.Warn,
                        string.Format(CultureInfo.InvariantCulture, "存在每帧托管分配（{0:0} B/帧），零分配目标未达成", alloc.value),
                        "即使单帧量小，长时间运行仍会累积触发 GC。已扣除编辑器自身开销。",
                        "定位每帧分配点：字符串拼接、装箱、闭包、容器扩容。", 0.7f);
                    Ev(f, "frame_capture", "项目每帧分配", Fmt(alloc.value), "B", "0 B", alloc.source);
                    outList.Add(f);
                }
            }

            var gc = s.FindMetric("GC 次数");
            if (gc != null && gc.value > 0 && s.frames.Count > 0)
            {
                var f = New("gc_frequency", "内存", Severity.Warn,
                    string.Format(CultureInfo.InvariantCulture, "采样窗口内触发 GC {0:0} 次（{1} 帧）", gc.value, s.frames.Count),
                    "GC 触发会造成主线程暂停，是帧尖峰的常见原因。",
                    "降低每帧分配量是最有效的办法；不要用 GC.Collect 手动触发。", 0.8f);
                Ev(f, "frame_capture", "GC 次数", Fmt(gc.value), "次", "0 次", string.Format(CultureInfo.InvariantCulture, "{0} 帧窗口", s.frames.Count));
                outList.Add(f);
            }

            // Temp Allocator：首尾对比判断是否单调增长（泄漏信号）
            var temp = s.FindMetric("TempAllocator");
            if (temp != null && temp.value > 0)
            {
                if (temp.value > b.maxTempAllocatorMB * 1024L * 1024L)
                {
                    var f = New("temp_allocator_over", "内存", Severity.Warn,
                        string.Format(CultureInfo.InvariantCulture, "Temp Allocator 占用 {0:0.0} MB，超过预算 {1} MB", temp.value / 1048576.0, b.maxTempAllocatorMB),
                        "Unity 的临时分配器主要用于原生容器，持续增长往往意味着原生侧泄漏（未 Dispose 的 NativeArray/Job 等）。",
                        "检查 NativeContainer 的 Dispose、JobHandle.Complete 与 ProfilerMarker 的释放。", 0.7f);
                    Ev(f, "memory", "TempAllocator", Fmt(temp.value / 1048576.0), "MB", b.maxTempAllocatorMB + " MB", "Profiler.GetTempAllocatorSize");
                    outList.Add(f);
                }
            }

            if (s.frames.Count >= 60)
            {
                long first = s.frames[0].tempAllocBytes;
                long last = s.frames[s.frames.Count - 1].tempAllocBytes;
                if (first > 0 && last > first * 2 && last - first > 4 * 1024 * 1024)
                {
                    var f = New("temp_allocator_growth", "内存", Severity.Warn,
                        string.Format(CultureInfo.InvariantCulture, "Temp Allocator 在采样窗口内从 {0:0.0} MB 增长到 {1:0.0} MB", first / 1048576.0, last / 1048576.0),
                        "单调增长而不回落是原生内存泄漏的典型信号，最终会导致 OOM。",
                        "排查 NativeArray/Job 生命周期，以及第三方原生插件（音视频、网络）的释放时机。", 0.65f);
                    Ev(f, "frame_capture", "TempAllocator 首帧", Fmt(first / 1048576.0), "MB", "", "采样窗口首帧");
                    Ev(f, "frame_capture", "TempAllocator 末帧", Fmt(last / 1048576.0), "MB", "", "采样窗口末帧");
                    outList.Add(f);
                }
            }
        }

        // =====================================================================
        // 内存总量
        // =====================================================================

        static void EvaluateMemory(List<PerfFinding> outList, PerfSnapshot s, PerfBudget b)
        {
            var total = s.FindMetric("总分配内存");
            if (total != null && b.maxTotalMemoryMB > 0 && total.value > b.maxTotalMemoryMB * 1024L * 1024L)
            {
                var f = New("memory_total_over", "内存", Severity.Warn,
                    string.Format(CultureInfo.InvariantCulture, "总分配内存 {0:0} MB，超过预算 {1} MB", total.value / 1048576.0, b.maxTotalMemoryMB),
                    "移动端内存超标会导致系统强杀进程。",
                    "按资源审计结果削减纹理内存（通常是最大头），再检查音频与网格。", 0.75f);
                Ev(f, "memory", "总分配内存", Fmt(total.value / 1048576.0), "MB", b.maxTotalMemoryMB + " MB", "Profiler.GetTotalAllocatedMemoryLong");
                outList.Add(f);
            }

            var texMem = s.FindMetric("纹理内存(估算)");
            if (texMem != null && b.maxTextureMemoryMB > 0 && texMem.value > b.maxTextureMemoryMB * 1024L * 1024L)
            {
                var f = New("texture_memory_over", "内存", Severity.Error,
                    string.Format(CultureInfo.InvariantCulture, "纹理内存估算 {0:0} MB，超过预算 {1} MB", texMem.value / 1048576.0, b.maxTextureMemoryMB),
                    "纹理通常是运行时内存占比最大的资源类型。",
                    "优先处理审计结果中的 Read/Write 开启、未压缩与超大 maxTextureSize 的纹理。", 0.8f);
                Ev(f, "audit_assets", "纹理内存(估算)", Fmt(texMem.value / 1048576.0), "MB", b.maxTextureMemoryMB + " MB", "按导入设置估算（含 mipmap 增量）");
                outList.Add(f);
            }
        }

        // =====================================================================
        // 渲染
        // =====================================================================

        static void EvaluateRendering(List<PerfFinding> outList, PerfSnapshot s, PerfBudget b)
        {
            AddOverBudget(outList, s, "Draw Calls", b.maxDrawCalls, "次", "渲染", "draw_calls_over",
                "Draw Call 过高说明合批被打断或材质/贴图种类过多。",
                "检查静态批处理标记、共享材质、图集化与小物件合并（GPU Instancing / SRP Batcher）。");

            AddOverBudget(outList, s, "SetPass Calls", b.maxSetPassCalls, "次", "渲染", "setpass_over",
                "SetPass Call 高说明着色器状态切换频繁，往往比 Draw Call 更致命。",
                "按材质排序、合并 Shader 变体、使用 MaterialPropertyBlock 而非多材质实例。");

            AddOverBudget(outList, s, "Triangles", b.maxTriangles, "个", "渲染", "triangles_over",
                "三角面数超标通常是模型 LOD 缺失或大量小物件未剔除。",
                "引入 LOD、遮挡剔除（Occlusion Culling）与距离裁剪。");
        }

        static void AddOverBudget(List<PerfFinding> outList, PerfSnapshot s, string metricName, double budget, string unit,
            string category, string id, string why, string how)
        {
            var m = s.FindMetric(metricName);
            if (m == null || budget <= 0 || m.value <= budget) return;

            string severity = m.value > budget * 1.5 ? Severity.Error : Severity.Warn;
            var f = New(id, category, severity,
                string.Format(CultureInfo.InvariantCulture, "{0} = {1:0} {2}，超过预算 {3:0}", metricName, m.value, unit, budget),
                why, how, m.value > budget * 2 ? 0.85f : 0.7f);
            f.jumpTo = "";
            Ev(f, "render_stats", metricName, Fmt(m.value), unit, budget.ToString(CultureInfo.InvariantCulture), m.source);
            outList.Add(f);
        }

        // =====================================================================
        // Marker 热点
        // =====================================================================

        static void EvaluateMarkers(List<PerfFinding> outList, PerfSnapshot s)
        {
            if (s.markers == null || s.markers.Count == 0) return;

            double total = 0;
            for (int i = 0; i < s.markers.Count; i++) total += s.markers[i].selfMs;
            if (total <= 0) return;

            var top = s.markers[0];
            double ratio = top.selfMs / total;
            if (ratio > 0.25 && top.selfMs > 1.0)
            {
                var f = New("marker_hotspot", "CPU", Severity.Warn,
                    string.Format(CultureInfo.InvariantCulture, "耗时集中在单个 Marker：{0}（自身 {1:0.##} ms，占 Top 采样 {2:P0}）", top.name, top.selfMs, ratio),
                    "热点集中说明优化方向明确，改动这一个点就能获得明显收益。",
                    "定位到该 marker 对应的模块/脚本，优先做算法或调用频率优化。", 0.7f);
                Ev(f, "profile_markers", "Top Marker 自身耗时", Fmt(top.selfMs), "ms", "占比 > 25% 视为集中", top.name);
                Ev(f, "profile_markers", "Top Marker 占用比", Fmt(ratio * 100), "%", "25%", "Top " + s.markers.Count + " 个 marker 合计 " + Fmt(total) + " ms");
                outList.Add(f);
            }

            // GC 分配最多的 marker
            var byGc = new List<MarkerStat>(s.markers);
            byGc.Sort((a, x) => x.gcAllocBytes.CompareTo(a.gcAllocBytes));
            if (byGc[0].gcAllocBytes > 4096)
            {
                var f = New("marker_gc_alloc", "内存", Severity.Warn,
                    string.Format(CultureInfo.InvariantCulture, "GC 分配集中在：{0}（{1:0.0} KB）", byGc[0].name, byGc[0].gcAllocBytes / 1024.0),
                    "直接给出每帧分配的具体位置，比泛泛的「少用 LINQ」有用得多。",
                    "打开该 marker 对应的代码，检查容器/字符串/委托分配。", 0.65f);
                Ev(f, "profile_markers", "Marker GC 分配", Fmt(byGc[0].gcAllocBytes / 1024.0), "KB", "4 KB", byGc[0].name);
                outList.Add(f);
            }
        }

        // =====================================================================
        // 资源 / 场景 / 物理
        // =====================================================================

        static void EvaluateAssets(List<PerfFinding> outList, PerfSnapshot s)
        {
            if (s.assetIssues == null || s.assetIssues.Count == 0) return;

            var groups = GroupBy(s.assetIssues, x => string.IsNullOrEmpty(x.code) ? x.issue : x.code);
            foreach (var g in groups)
            {
                var first = g.Value[0];
                string code = first.code == null ? "" : first.code;
                // 按 code 分组：同一类问题不会再因为文案里的数值不同而被拆成多条结论
                string groupKey = string.IsNullOrEmpty(code) ? first.issue : code;
                var f = New("asset_" + Sanitize(groupKey), "资源", Severity.Rank(first.severity) >= 3 ? Severity.Error : first.severity,
                    string.Format(CultureInfo.InvariantCulture, "{0}（{1} 个资源）", first.issue, g.Value.Count),
                    "同一类导入问题批量出现时，通常可以用一次批量修改解决。",
                    first.suggestion, g.Value.Count > 5 ? 0.9f : 0.7f);
                f.fixCode = code;
                f.jumpTo = first.path;

                int shown = 0;
                long totalMem = 0;
                for (int i = 0; i < g.Value.Count; i++)
                {
                    totalMem += g.Value[i].estimatedMemoryBytes;
                    if (shown++ < 5) Ev(f, "audit_assets", "示例资源", g.Value[i].path, "", "", g.Value[i].path);
                }
                Ev(f, "audit_assets", "问题资源数", g.Value.Count.ToString(CultureInfo.InvariantCulture), "个", "0 个", "Assets/");
                if (totalMem > 0) Ev(f, "audit_assets", "估算内存合计", Fmt(totalMem / 1048576.0), "MB", "", "受影响资源");
                outList.Add(f);
            }
        }

        static void EvaluateScene(List<PerfFinding> outList, PerfSnapshot s)
        {
            if (s.sceneIssues == null || s.sceneIssues.Count == 0) return;

            var groups = GroupBy(s.sceneIssues, x => string.IsNullOrEmpty(x.code) ? x.issue : x.code);
            foreach (var g in groups)
            {
                var first = g.Value[0];
                string code = first.code == null ? "" : first.code;
                string groupKey = string.IsNullOrEmpty(code) ? first.issue : code;
                var f = New("scene_" + Sanitize(groupKey), "场景",
                    Severity.Rank(first.severity) >= 3 ? Severity.Error : first.severity,
                    string.Format(CultureInfo.InvariantCulture, "{0}（{1} 处）", first.issue, g.Value.Count),
                    "组件级反模式，通常可以脚本化批量修复。",
                    first.suggestion, 0.7f);
                f.fixCode = code;
                f.jumpTo = first.hierarchyPath;

                for (int i = 0; i < g.Value.Count && i < 5; i++)
                    Ev(f, "audit_scene", "示例对象", g.Value[i].hierarchyPath, "", "", g.Value[i].componentType);
                Ev(f, "audit_scene", "出现次数", g.Value.Count.ToString(CultureInfo.InvariantCulture), "处", "0 处", "当前场景");
                outList.Add(f);
            }
        }

        static void EvaluatePhysics(List<PerfFinding> outList, PerfSnapshot s)
        {
            var m = s.FindMetric("MeshCollider");
            if (m != null && m.value > 0)
            {
                var f = New("physics_meshcollider", "物理", Severity.Info,
                    string.Format(CultureInfo.InvariantCulture, "场景内 MeshCollider {0:0} 个", m.value),
                    "MeshCollider 的碰撞检测与构建成本都明显高于基础碰撞体。",
                    "静态物体保留非凸 MeshCollider，动态物体改用凸包或基础碰撞体组合。", 0.5f);
                Ev(f, "audit_physics", "MeshCollider", Fmt(m.value), "个", "0 个", "当前场景");
                outList.Add(f);
            }
        }

        // =====================================================================
        // 代码
        // =====================================================================

        static void EvaluateCode(List<PerfFinding> outList, PerfSnapshot s)
        {
            if (s.codeIssues == null || s.codeIssues.Count == 0) return;

            var groups = GroupBy(s.codeIssues, x => x.pattern);
            foreach (var g in groups)
            {
                var first = g.Value[0];
                var f = New("code_" + Sanitize(first.pattern), "代码",
                    Severity.Rank(first.severity) >= 3 ? Severity.Error : first.severity,
                    string.Format(CultureInfo.InvariantCulture, "每帧方法中出现 {0}（{1} 处）", first.pattern, g.Value.Count),
                    "这些写法在 Update/回调中每帧执行，是每帧分配与隐性开销的主要来源。",
                    first.suggestion, Math.Min(0.9f, 0.5f + g.Value.Count * 0.05f));
                f.fixCode = CodeIssue.BasePattern(first.pattern);
                f.jumpTo = first.file + ":" + first.line;

                int shown = 0;
                for (int i = 0; i < g.Value.Count && shown < 6; i++)
                {
                    var c = g.Value[i];
                    Ev(f, "scan_scripts", "位置", c.file + ":" + c.line, "", "", c.snippet);
                    shown++;
                }
                Ev(f, "scan_scripts", "出现次数", g.Value.Count.ToString(CultureInfo.InvariantCulture), "处", "0 处", "Assets/**/*.cs");
                outList.Add(f);
            }
        }

        // =====================================================================
        // 配置
        // =====================================================================

        static void EvaluateConfiguration(List<PerfFinding> outList, PerfSnapshot s)
        {
            if (s.targetFrameRate == -1 && s.vSyncCount == 0)
            {
                var f = New("config_no_framelimit", "帧率", Severity.Info,
                    "未限制帧率（targetFrameRate = -1 且 VSync = 0）",
                    "编辑器与开发机上会跑满 CPU/GPU，测得的帧耗时数据不代表目标设备表现，也掩盖真实瓶颈。",
                    "测试时设置 Application.targetFrameRate = 目标帧率，或开启 VSync。", 0.8f);
                Ev(f, "env_info", "targetFrameRate", "-1", "", "目标帧率值", "Application.targetFrameRate");
                Ev(f, "env_info", "vSyncCount", "0", "级", "0 级", "QualitySettings.vSyncCount");
                outList.Add(f);
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        static Dictionary<string, List<T>> GroupBy<T>(List<T> items, Func<T, string> keySelector)
        {
            var map = new Dictionary<string, List<T>>();
            for (int i = 0; i < items.Count; i++)
            {
                string key = keySelector(items[i]);
                if (string.IsNullOrEmpty(key)) key = "(未分类)";
                List<T> list;
                if (!map.TryGetValue(key, out list))
                {
                    list = new List<T>();
                    map[key] = list;
                }
                list.Add(items[i]);
            }
            return map;
        }

        static PerfFinding New(string id, string category, string severity, string title, string detail, string recommendation, float confidence)
        {
            var f = new PerfFinding();
            f.id = id;
            f.category = category;
            f.severity = severity;
            f.title = title;
            f.detail = detail;
            f.recommendation = recommendation;
            f.confidence = confidence;
            return f;
        }

        static void Ev(PerfFinding f, string tool, string metric, string value, string unit, string threshold, string source)
        {
            f.evidence.Add(new PerfEvidence(tool, metric, value, unit, threshold, source));
        }

        static string Fmt(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "issue";
            var chars = new char[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                chars[i] = (char.IsLetterOrDigit(c) || c == '_') ? c : '_';
            }
            return new string(chars);
        }
    }
}

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
            EvaluateSampleSize(findings, s, b);

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
        // 采集质量
        // =====================================================================

        /// <summary>
        /// 采集窗口太短时不产统计类结论，而是明说为什么。
        ///
        /// 实测踩过：跟随采集只跑了 10 帧（约 0.7 秒），工具照样给出
        /// 「均值 2.28 / P50 2.27 / P95 2.3 / 峰值 2.3 ms」与「FPS 438」——
        /// 2 帧算出来的「P95」就是那 2 帧里的一个，看着精确，实际毫无意义。
        /// 宁可说「样本不够」，也不编一个精确的假数字。
        /// </summary>
        static void EvaluateSampleSize(List<PerfFinding> outList, PerfSnapshot s, PerfBudget b)
        {
            int window = s.WindowFrames();
            if (window <= 0 || window >= PerfSnapshot.MinFramesForStats) return;

            var f = New("sample_too_small", "采集", Severity.Info,
                string.Format(CultureInfo.InvariantCulture, "采集窗口只有 {0} 帧，统计类结论已跳过", window),
                string.Format(CultureInfo.InvariantCulture,
                    "均值 / P95 / 峰值 / 每帧分配这类量需要足够多的帧才有意义（门槛 {0} 帧）。"
                    + "本次窗口只录到 {1} 帧，数值会被个别帧主导，所以规则侧不下判断；"
                    + "资源、场景、代码这类不依赖帧数据的审计结论不受影响，照常给出。",
                    PerfSnapshot.MinFramesForStats, window),
                "跟随采集时多操作几秒（建议 ≥5 秒）：它会一直录到你退出 Play 或手动停止。", 0.9f);
            Ev(f, "frame_capture", "采集窗口帧数", window.ToString(CultureInfo.InvariantCulture), "帧",
                PerfSnapshot.MinFramesForStats + " 帧", "Profiler 面板帧历史（窗口起止帧号之差）");
            outList.Add(f);
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
            string src = string.Format(CultureInfo.InvariantCulture, "Profiler 面板/{0} 帧（逐帧明细为抽样）", s.frames.Count);

            // 合理性闸门：读数荒谬时不产出任何帧率结论。
            // 实测踩过：面板列语义错位，把一列字节数当成耗时列，算出 36 亿 ms/帧，
            // 然后被报成「严重：帧耗时 P95 超出预算」。宁可没有结论，也不输出这种垃圾。
            if (avgM.value > MaxPlausibleFrameMs || p95 > MaxPlausibleFrameMs || max > MaxPlausibleFrameMs)
            {
                s.AddNote(string.Format(CultureInfo.InvariantCulture,
                    "帧耗时读数不合理（均值 {0:0.#} ms / P95 {1:0.#} ms / 峰值 {2:0.#} ms，上限 {3:0} ms），"
                    + "已跳过全部帧率类结论。这通常是数据来源的列语义对不上 —— 先跑 Tools/PerfAgent/API 探针 核对列内容。",
                    avgM.value, p95, max, MaxPlausibleFrameMs));
                return;
            }

            // 样本量闸门：窗口太短时均值/P95/峰值会被个别帧主导（实测出现过 2 帧算出
            // 「P50 2.27 / P95 2.3 / 峰值 2.3」这种看着精确、实际毫无意义的数字），
            // 所以这里不下结论，改为由 EvaluateSampleSize 统一说明原因。
            if (s.WindowTooSmallForStats()) return;

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

            // 抖动：必须有「游戏侧」的佐证才归因到项目。
            //
            // 编辑器里量到的帧耗时是编辑器 update 的墙钟间隔，包含编辑器自身的停顿
            //（资源刷新、窗口重绘、编辑器 GC）。实测：一个「空场景 + 无脚本 + 24 个 Draw Call」
            // 的工程照样能出现 87 ms 的孤立尖峰，且尖峰帧的分配/Draw Call 与普通帧完全一样 ——
            // 那是编辑器的停顿，不是项目的问题。没有佐证就只写一句提示，不报结论。
            if (s.frames.Count >= 30)
            {
                var spikes = s.SpikeFrames(20);
                int slowCount = CountFramesAbove(s, p95 * 1.5);
                int needCount = Math.Max(10, (int)(s.frames.Count * 0.02));

                if (spikes.Count >= 3 && p95 > 0 && max > p95 * 1.5 && max >= JitterFloorMs)
                {
                    double spikeAlloc, baseAlloc, spikeDc, baseDc;
                    bool recorderAlloc;
                    bool corroborated = CorrelateSpikes(s, spikes, out spikeAlloc, out baseAlloc,
                                                        out spikeDc, out baseDc, out recorderAlloc);

                    if (corroborated || slowCount >= needCount)
                    {
                        var f = New("frame_time_jitter", "帧率", Severity.Warn,
                            string.Format(CultureInfo.InvariantCulture, "帧耗时抖动明显：峰值 {0:0.##} ms 是 P95（{1:0.##} ms）的 {2:0.#} 倍", max, p95, max / p95),
                            string.Format(CultureInfo.InvariantCulture,
                                "共 {0} 帧（{1:0.#}%）明显超过 P95×1.5；{2}稳态帧率尚可但存在卡顿尖峰。",
                                slowCount, 100.0 * slowCount / s.frames.Count,
                                corroborated ? "尖峰帧的分配/Draw Call 与普通帧有差异，" : "尖峰反复出现，"),
                            "对比尖峰帧与平均帧的指标差异（get_frames 工具），定位尖峰来源。", 0.75f);
                        Ev(f, "frame_capture", "帧耗时峰值", Fmt(max), "ms", "P95×1.5 = " + Fmt(p95 * 1.5), src);
                        Ev(f, "frame_capture", "帧耗时 P95", Fmt(p95), "ms", "", src);
                        Ev(f, "frame_capture", "超过 P95×1.5 的帧数", slowCount.ToString(CultureInfo.InvariantCulture),
                            "帧", "门槛 " + needCount.ToString(CultureInfo.InvariantCulture) + " 帧", src);
                        outList.Add(f);

                        if (corroborated)
                        {
                            var attribution = AttributeSpikes(s, spikes, spikeAlloc, baseAlloc, spikeDc, baseDc, recorderAlloc);
                            if (attribution != null) outList.Add(attribution);
                        }
                        else
                        {
                            s.AddNote(string.Format(CultureInfo.InvariantCulture,
                                "尖峰反复出现（{0} 帧，峰值 {1:0.##} ms），但尖峰帧的分配与 Draw Call 与普通帧相当，"
                                + "游戏侧看不到成因，未归因到项目。",
                                slowCount, max));
                        }
                    }
                    else
                    {
                        s.AddNote(string.Format(CultureInfo.InvariantCulture,
                            "检测到 {0} 帧明显偏慢（峰值 {1:0.##} ms vs P95 {2:0.##} ms），但尖峰帧的分配与 Draw Call 与普通帧相当，"
                            + "在编辑器内无法归因到项目：编辑器自身的停顿（资源刷新、窗口重绘、编辑器 GC、首次 Shader 编译）"
                            + "也会产生这种孤立尖峰，本工具不把这种尖峰算成项目问题。要确认请在 Development Build 或真机上复测。",
                            slowCount, max, p95));
                    }
                }
            }
        }

        /// <summary>低于 30 FPS 的帧才叫「卡顿尖峰」；比这更快的帧不构成需要报警的抖动。</summary>
        const double JitterFloorMs = 33.0;
        /// <summary>
        /// 单帧耗时的合理上限（毫秒）。超过它一律当成「读数不对」而不是「游戏很卡」：
        /// 2 秒一帧已经离谱到不可能是真实帧耗时，只可能是列语义错位读错了列。
        /// </summary>
        public const double MaxPlausibleFrameMs = 2000.0;
        static int CountFramesAbove(PerfSnapshot s, double thresholdMs)
        {
            int n = 0;
            for (int i = 0; i < s.frames.Count; i++)
                if (s.frames[i].deltaMs > thresholdMs) n++;
            return n;
        }

        /// <summary>
        /// 把尖峰帧的分配与 Draw Call 跟其余帧对比，作为「尖峰是否属于项目」的佐证。
        ///
        /// 分配优先用 ProfilerRecorder「GC Allocated In Frame」（与主指标同源），
        /// 没有时退回 GC.GetTotalMemory 差值（弱口径，会在证据里写明）。
        /// 差异要越过噪声带才算数：编辑器每帧本来就有一两千字节级的波动。
        /// </summary>
        static bool CorrelateSpikes(PerfSnapshot s, List<int> spikeFrames,
            out double spikeAlloc, out double baseAlloc,
            out double spikeDc, out double baseDc, out bool recorderAlloc)
        {
            recorderAlloc = s.HasRecorderGcAlloc();
            spikeAlloc = baseAlloc = spikeDc = baseDc = 0;
            int ns = 0, nb = 0;

            for (int i = 0; i < s.frames.Count; i++)
            {
                var fr = s.frames[i];
                double alloc = recorderAlloc ? Math.Max(0, (double)fr.allocInFrameBytes) : Math.Max(0, fr.managedAllocBytes);
                if (spikeFrames.Contains(fr.frame)) { spikeAlloc += alloc; spikeDc += fr.drawCalls; ns++; }
                else { baseAlloc += alloc; baseDc += fr.drawCalls; nb++; }
            }
            if (ns == 0 || nb == 0) return false;

            spikeAlloc /= ns; baseAlloc /= nb;
            spikeDc /= ns; baseDc /= nb;

            bool allocSignal = spikeAlloc - baseAlloc > Math.Max(4096.0, baseAlloc * 0.25);
            bool dcSignal = spikeDc - baseDc >= 1 && spikeDc > baseDc * 1.25;
            return allocSignal || dcSignal;
        }

        /// <summary>尖峰归因：把尖峰帧的渲染/分配指标与普通帧对比。
        /// 数字由 CorrelateSpikes 算好传入 —— 它同时决定了这次归因能不能取证。</summary>
        static PerfFinding AttributeSpikes(PerfSnapshot s, List<int> spikeFrames,
            double spikeAlloc, double baseAlloc, double spikeDcAvg, double baseDcAvg, bool recorderAlloc)
        {
            if (spikeFrames.Count == 0) return null;
            string frameList = string.Join(",", spikeFrames.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());

            bool allocSignal = spikeAlloc - baseAlloc > Math.Max(4096.0, baseAlloc * 0.25);
            string allocSource = recorderAlloc
                ? "ProfilerRecorder「GC Allocated In Frame」（含编辑器开销）"
                : "GC.GetTotalMemory 差值（弱口径，看不见当帧分配后即回收的部分）";

            var f = New("spike_attribution", "帧率", Severity.Info,
                "尖峰帧归因",
                string.Format(CultureInfo.InvariantCulture,
                    "尖峰帧（{0}）相对普通帧：每帧分配 {1:0} B vs {2:0} B，Draw Call {3:0.#} vs {4:0.#}。",
                    frameList, spikeAlloc, baseAlloc, spikeDcAvg, baseDcAvg),
                allocSignal
                    ? "分配量差异显著，尖峰很可能由 GC / 临时分配引起，优先排查尖峰帧上的字符串、容器与协程分配。"
                    : "分配量差异不明显，更可能是同步加载、物理集中计算或 Shader 变体首次编译造成。",
                allocSignal ? 0.6f : 0.5f);
            Ev(f, "frame_capture", "尖峰帧平均分配", Fmt(spikeAlloc), "B", "普通帧 " + Fmt(baseAlloc) + " B",
                allocSource + "；帧 " + frameList);
            Ev(f, "frame_capture", "尖峰帧平均 Draw Call", Fmt(spikeDcAvg), "次", "普通帧 " + Fmt(baseDcAvg) + " 次",
                "帧 " + frameList);
            return f;
        }

        // =====================================================================
        // 分配与 GC
        // =====================================================================

        /// <summary>
        /// 会产生每帧分配的脚本反模式 id（见 ScriptAntipatternCollector 的 Patterns）。
        /// 命中它们意味着项目脚本侧确实存在稳态分配点。
        /// </summary>
        static readonly string[] AllocPatternIds =
        {
            "gc_collection_new", "gc_array_new", "gc_string_concat", "gc_string_format",
            "linq", "foreach_enumerator", "new_waitfor", "stringbuilder_new", "sort_alloc", "debug_log"
        };

        /// <summary>
        /// 脚本扫描里命中了几处「每帧分配」写法。
        ///
        /// 它只是**佐证**，不是闸门：命中可以提高置信度，没命中也不能断定没问题
        ///（第三方代码、反射调用、协程里的闭包都扫不到），只是要在结论里如实说明。
        /// </summary>
        static int CountAllocPatterns(PerfSnapshot s)
        {
            if (s.codeIssues == null) return 0;

            int n = 0;
            for (int i = 0; i < s.codeIssues.Count; i++)
            {
                string p = s.codeIssues[i].pattern;
                if (string.IsNullOrEmpty(p)) continue;
                for (int k = 0; k < AllocPatternIds.Length; k++)
                {
                    if (p.IndexOf(AllocPatternIds[k], StringComparison.Ordinal) >= 0) { n++; break; }
                }
            }
            return n;
        }

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
                // 三道闸门，缺一不报：
                //   1. 播放器预算（用户设的稳态每帧分配上限）
                //   2. 基线噪声带 —— 基线与 Play 模式的实际开销不会完全一致，留一条带子
                //   3. 归因地板 —— 编辑器在 Play 模式下自身就要分配的量级。
                //      实测：空场景（无用户脚本）在编辑器 Play 下是 14435 B/帧，而编辑模式空闲基线只有 465 B/帧，
                //      两者不是一个量级 —— 残差落在地板以下时，无法把它与编辑器开销区分开，报了就是假问题。
                double noise = baseline == null ? double.MaxValue : Math.Max(8192.0, baseline.value * 0.25);
                double floor = Math.Max(Math.Max(b.maxManagedAllocBytesPerFrame, noise), b.editorPlayModeOverheadBytes);

                if (alloc.value > floor && !s.WindowTooSmallForStats())
                {
                    int allocPatterns = CountAllocPatterns(s);
                    string corroboration = allocPatterns > 0
                        ? string.Format(CultureInfo.InvariantCulture,
                            "脚本扫描同时找到 {0} 处「每帧分配」类写法，与读数一致。", allocPatterns)
                        : "脚本扫描未找到每帧分配写法（第三方代码、反射调用扫不到），建议再用 Player 构建版复核一遍。";

                    var f = New("gc_alloc_per_frame", "内存", Severity.Error,
                        string.Format(CultureInfo.InvariantCulture, "项目每帧托管分配约 {0:0} B，超出预算 {1} B",
                            alloc.value, b.maxManagedAllocBytesPerFrame),
                        string.Format(CultureInfo.InvariantCulture,
                            "已扣除编辑器自身开销（实测 {0:0} B/帧 − 基线 {1:0} B/帧），且越过归因地板 {2:0} B/帧。"
                            + "稳态每帧分配会导致 GC 周期性触发，表现为规律性卡顿尖峰。{3}",
                            rawAlloc == null ? 0 : rawAlloc.value,
                            baseline == null ? 0 : baseline.value,
                            b.editorPlayModeOverheadBytes, corroboration),
                        "扫描脚本反模式列表（get_code_issues），优先处理每帧 new 容器/字符串/LINQ 的写法。",
                        allocPatterns > 0 ? 0.8f : 0.6f);
                    Ev(f, "frame_capture", "项目每帧分配", Fmt(alloc.value), "B",
                        b.maxManagedAllocBytesPerFrame.ToString(CultureInfo.InvariantCulture), alloc.source);
                    if (rawAlloc != null)
                        Ev(f, "frame_capture", "每帧托管分配（含编辑器开销）", Fmt(rawAlloc.value), "B", "", rawAlloc.source);
                    if (baseline != null)
                        Ev(f, "frame_capture", "编辑器开销基线", Fmt(baseline.value), "B", "", baseline.source);
                    Ev(f, "frame_capture", "归因地板（编辑器 Play 模式开销上界）",
                        b.editorPlayModeOverheadBytes.ToString(CultureInfo.InvariantCulture), "B",
                        "残差必须越过它才能归因到项目", "PerfBudget.editorPlayModeOverheadBytes（空场景实测 14435 B/帧）");
                    outList.Add(f);
                }
                else if (alloc.value > b.maxManagedAllocBytesPerFrame)
                {
                    // 越过预算但过不了闸门：不报结论，但要写清楚为什么 ——
                    // 否则用户会以为「数字这么吓人，工具却什么都不说」。
                    s.AddNote(string.Format(CultureInfo.InvariantCulture,
                        "项目每帧分配估算 {0:0} B/帧 虽然超过播放器预算 {1} B，但未越过归因闸门"
                        + "（归因地板 {2:0} B/帧、基线噪声带 {3} B/帧{4}）：在编辑器里它无法与编辑器自身的 "
                        + "Play 模式开销区分开，因此本次不报为项目问题。要确认真实分配，请用 Player 构建版复核（构建里没有编辑器开销）。",
                        alloc.value, b.maxManagedAllocBytesPerFrame, b.editorPlayModeOverheadBytes, noise,
                        s.WindowTooSmallForStats() ? "、且窗口帧数不足 " + PerfSnapshot.MinFramesForStats + " 帧" : ""));
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
            // Draw Call 的窗口均值叫「Draw Calls 均值」（面板序列口径），单点读数叫「Draw Calls」。
            // 均值覆盖整段窗口，比单帧读数可信，优先用它 —— 否则面板序列一改名，这条规则就会静默失效。
            AddOverBudget(outList, s, "Draw Calls", "Draw Calls 均值", b.maxDrawCalls, "次", "渲染", "draw_calls_over",
                "Draw Call 过高说明合批被打断或材质/贴图种类过多。",
                "检查静态批处理标记、共享材质、图集化与小物件合并（GPU Instancing / SRP Batcher）。");

            AddOverBudget(outList, s, "SetPass Calls", null, b.maxSetPassCalls, "次", "渲染", "setpass_over",
                "SetPass Call 高说明着色器状态切换频繁，往往比 Draw Call 更致命。",
                "按材质排序、合并 Shader 变体、使用 MaterialPropertyBlock 而非多材质实例。");

            AddOverBudget(outList, s, "Triangles", null, b.maxTriangles, "个", "渲染", "triangles_over",
                "三角面数超标通常是模型 LOD 缺失或大量小物件未剔除。",
                "引入 LOD、遮挡剔除（Occlusion Culling）与距离裁剪。");
        }

        /// <summary>
        /// 预算对比。windowedName 是同一计数器的「整段窗口均值」指标名（可为 null）：
        /// 面板序列覆盖窗口内每一帧，比 ProfilerRecorder 的单点读数可信，有它就优先用。
        /// </summary>
        static void AddOverBudget(List<PerfFinding> outList, PerfSnapshot s, string metricName, string windowedName,
            double budget, string unit, string category, string id, string why, string how)
        {
            var m = string.IsNullOrEmpty(windowedName) ? null : s.FindMetric(windowedName);
            if (m == null) m = s.FindMetric(metricName);
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

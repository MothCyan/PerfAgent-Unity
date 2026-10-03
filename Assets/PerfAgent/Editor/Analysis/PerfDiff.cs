using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>指标变化方向：判定「变大」是好是坏必须显式声明，不能留给读者猜。</summary>
    public static class DiffDirection
    {
        public const string Cost = "cost";        // 越小越好：帧耗时 / 分配 / Draw Call / 内存
        public const string Benefit = "benefit";  // 越大越好：FPS / 帧率
        public const string Neutral = "neutral";  // 无明确方向：资源数量等规模指标
    }

    /// <summary>单个指标在两个快照之间的变化。deltaPercent 为 NaN 表示基准为 0，无法算比例。</summary>
    public class MetricDelta
    {
        public string name = "";
        public string unit = "";
        public double baseline = double.NaN;
        public double current = double.NaN;
        public double delta;
        public double deltaPercent = double.NaN;
        public string direction = DiffDirection.Neutral;
        public string baselineSeverity = Severity.Info;
        public string currentSeverity = Severity.Info;
        public bool onlyInCurrent;
        public bool onlyInBaseline;
        public bool changed;      // 变化幅度超过显著性阈值
        public bool regressed;    // 朝坏的方向变化
        public bool improved;     // 朝好的方向变化

        public string Format()
        {
            if (onlyInCurrent) return name + "：仅当前快照存在（" + Num(current) + unit + "）";
            if (onlyInBaseline) return name + "：仅基准快照存在（" + Num(baseline) + unit + "）";

            string arrow = changed ? (regressed ? "↑ 恶化" : improved ? "↓ 改善" : "→ 变动") : "＝ 持平";
            var sb = new StringBuilder();
            sb.Append(name).Append("：").Append(Num(baseline)).Append(unit)
              .Append(" → ").Append(Num(current)).Append(unit)
              .Append("（").Append(arrow).Append(' ');
            if (delta >= 0) sb.Append('+');
            sb.Append(Num(delta));
            if (!double.IsNaN(deltaPercent))
                sb.Append(" / ").Append(deltaPercent.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)).Append('%');
            sb.Append("）");
            return sb.ToString();
        }

        static string Num(double v)
        {
            if (double.IsNaN(v)) return "N/A";
            if (Math.Abs(v) >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "G";
            if (Math.Abs(v) >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (Math.Abs(v) >= 1e4) return (v / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "k";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 两个快照的结构化差异。纯逻辑、不依赖 UnityEngine，因此可被独立回归与 Unity EditMode 测试同时覆盖。
    /// </summary>
    public class PerfDiffResult
    {
        public string baselineId = "";
        public string currentId = "";
        /// <summary>非空表示两个快照不可对比（缺失、同一份、或没有共同指标）。</summary>
        public string error = "";

        public List<MetricDelta> metrics = new List<MetricDelta>();
        public List<PerfFinding> newFindings = new List<PerfFinding>();
        public List<PerfFinding> resolvedFindings = new List<PerfFinding>();
        public List<PerfFinding> worsenedFindings = new List<PerfFinding>();
        public List<PerfFinding> improvedFindings = new List<PerfFinding>();
        public List<PerfFinding> persistingFindings = new List<PerfFinding>();

        public List<AssetIssue> newAssetIssues = new List<AssetIssue>();
        public List<AssetIssue> resolvedAssetIssues = new List<AssetIssue>();
        public List<SceneIssue> newSceneIssues = new List<SceneIssue>();
        public List<SceneIssue> resolvedSceneIssues = new List<SceneIssue>();
        public List<CodeIssue> newCodeIssues = new List<CodeIssue>();
        public List<CodeIssue> resolvedCodeIssues = new List<CodeIssue>();

        public bool Comparable { get { return string.IsNullOrEmpty(error); } }

        public int RegressedMetricCount { get { return Count(true); } }
        public int ImprovedMetricCount { get { return Count(false); } }
        public int SignificantlyChangedCount { get { return RegressedMetricCount + ImprovedMetricCount; } }

        int Count(bool regressed)
        {
            int n = 0;
            for (int i = 0; i < metrics.Count; i++)
            {
                var m = metrics[i];
                if (!m.changed) continue;
                if (regressed ? m.regressed : m.improved) n++;
            }
            return n;
        }

        /// <summary>
        /// 严重度加权分：正数表示整体变差，负数表示整体变好。
        /// 权重显式固定（error=3 / warn=1 / info=0，指标变化至少计 1 分），保证同样的输入永远得到同样的判定。
        /// 指标权重取 Math.Max(1, 严重度权重)，因为即使严重度为 info，一个超过显著性的指标变化也是有意义的信号。
        /// </summary>
        public int Score()
        {
            int score = 0;

            for (int i = 0; i < metrics.Count; i++)
            {
                var m = metrics[i];
                if (!m.changed) continue;
                if (m.regressed) score += Math.Max(1, Weight(m.currentSeverity));
                else if (m.improved) score -= Math.Max(1, Weight(m.baselineSeverity));
            }

            for (int i = 0; i < newFindings.Count; i++) score += Weight(newFindings[i].severity);
            for (int i = 0; i < resolvedFindings.Count; i++) score -= Weight(resolvedFindings[i].severity);
            for (int i = 0; i < worsenedFindings.Count; i++) score += 1;
            for (int i = 0; i < improvedFindings.Count; i++) score -= 1;
            for (int i = 0; i < newAssetIssues.Count; i++) score += Weight(newAssetIssues[i].severity);
            for (int i = 0; i < resolvedAssetIssues.Count; i++) score -= Weight(resolvedAssetIssues[i].severity);
            for (int i = 0; i < newSceneIssues.Count; i++) score += Weight(newSceneIssues[i].severity);
            for (int i = 0; i < resolvedSceneIssues.Count; i++) score -= Weight(resolvedSceneIssues[i].severity);
            for (int i = 0; i < newCodeIssues.Count; i++) score += Weight(newCodeIssues[i].severity);
            for (int i = 0; i < resolvedCodeIssues.Count; i++) score -= Weight(resolvedCodeIssues[i].severity);

            return score;
        }

        static int Weight(string severity)
        {
            if (severity == Severity.Error) return 3;
            if (severity == Severity.Warn) return 1;
            return 0;
        }

        public string Verdict()
        {
            if (!Comparable) return "不可对比";
            int score = Score();
            if (score >= 4) return "明显恶化";
            if (score >= 1) return "轻微恶化";
            if (score <= -4) return "明显改善";
            if (score <= -1) return "轻微改善";
            return "基本持平";
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.Append("### 快照对比：").Append(baselineId).Append(" → ").Append(currentId).Append('\n');
            if (!Comparable) { sb.Append('\n').Append(error).Append('\n'); return sb.ToString(); }

            sb.Append("\n**判定**：").Append(Verdict()).Append("（严重度加权分 ").Append(Score()).Append("）\n");
            sb.Append("\n- 恶化指标 ").Append(RegressedMetricCount)
              .Append(" 项，改善指标 ").Append(ImprovedMetricCount)
              .Append(" 项，新增结论 ").Append(newFindings.Count)
              .Append(" 条，消除结论 ").Append(resolvedFindings.Count).Append(" 条\n");

            if (SignificantlyChangedCount > 0)
            {
                sb.Append("\n#### 指标变化（仅列出超过显著性阈值的项）\n");
                for (int i = 0; i < metrics.Count; i++)
                {
                    var m = metrics[i];
                    if (!m.changed) continue;
                    sb.Append("- ").Append(m.Format()).Append('\n');
                }
            }

            AppendFindings(sb, "新增结论（回归）", newFindings);
            AppendFindings(sb, "结论加重", worsenedFindings);
            AppendFindings(sb, "结论减轻", improvedFindings);
            AppendFindings(sb, "已消除结论", resolvedFindings);

            if (persistingFindings.Count > 0)
                sb.Append("\n#### 仍然存在的结论\n- 共 ").Append(persistingFindings.Count).Append(" 条未发生变化\n");

            AppendIssues(sb, "新增资源问题", newAssetIssues, delegate (AssetIssue a) { return a.path + " - " + a.issue; });
            AppendIssues(sb, "已消除资源问题", resolvedAssetIssues, delegate (AssetIssue a) { return a.path + " - " + a.issue; });
            AppendIssues(sb, "新增场景问题", newSceneIssues, delegate (SceneIssue a) { return a.hierarchyPath + " - " + a.issue; });
            AppendIssues(sb, "已消除场景问题", resolvedSceneIssues, delegate (SceneIssue a) { return a.hierarchyPath + " - " + a.issue; });
            AppendIssues(sb, "新增代码问题", newCodeIssues, delegate (CodeIssue a) { return a.file + ":" + a.line + " - " + a.pattern; });
            AppendIssues(sb, "已消除代码问题", resolvedCodeIssues, delegate (CodeIssue a) { return a.file + ":" + a.line + " - " + a.pattern; });

            return sb.ToString();
        }

        static void AppendFindings(StringBuilder sb, string title, List<PerfFinding> list)
        {
            if (list.Count == 0) return;
            sb.Append("\n#### ").Append(title).Append('\n');
            for (int i = 0; i < list.Count; i++)
                sb.Append("- [").Append(list[i].severity).Append("] ").Append(list[i].title).Append('\n');
        }

        static void AppendIssues<T>(StringBuilder sb, string title, List<T> list, Func<T, string> format)
        {
            if (list.Count == 0) return;
            sb.Append("\n#### ").Append(title).Append('\n');
            for (int i = 0; i < list.Count; i++)
                sb.Append("- ").Append(format(list[i])).Append('\n');
        }
    }

    /// <summary>
    /// 快照对比引擎。回答三个问题：哪些指标更差/更好、哪些结论新增/消除、整体是进步还是退步。
    /// 刻意不依赖 UnityEngine，保证结论可由测试确定性复现。
    /// </summary>
    public static class PerfDiff
    {
        /// <summary>变化显著性阈值：变化幅度小于 2% 视为噪声，不计入改善/恶化。</summary>
        public const double SignificanceThreshold = 0.02;

        static readonly string[] CostKeywords =
        {
            "帧耗时", "耗时", "托管分配", "每帧分配", "Draw Call", "SetPass", "Triangles", "三角面",
            "内存", "TempAllocator", "GC", "批次", "Batches", "未静态", "延迟", "Latency"
        };

        static readonly string[] BenefitKeywords = { "fps", "FPS", "帧率", "帧数", "得分", "Score" };

        public static PerfDiffResult Compare(PerfSnapshot baseline, PerfSnapshot current)
        {
            var result = new PerfDiffResult();
            if (baseline == null && current == null) { result.error = "基准与当前快照均为空。"; return result; }
            if (baseline == null) { result.error = "基准快照为空，无法对比。"; return result; }
            if (current == null) { result.error = "当前快照为空，无法对比。"; return result; }

            result.baselineId = baseline.Label();
            result.currentId = current.Label();

            if (baseline == current || SameIdentity(baseline, current))
            {
                result.error = "基准与当前指向同一份快照（" + result.baselineId + "），对比没有意义。";
                return result;
            }

            DiffMetrics(baseline, current, result);
            DiffFindings(baseline, current, result);
            DiffIssues(baseline, current, result);

            if (result.metrics.Count == 0 && result.newFindings.Count == 0 && result.resolvedFindings.Count == 0)
                result.error = "两个快照没有可比对的指标或结论（可能是采集来源不同）。";

            return result;
        }

        static bool SameIdentity(PerfSnapshot a, PerfSnapshot b)
        {
            return !string.IsNullOrEmpty(a.id)
                && a.id == b.id
                && !string.IsNullOrEmpty(a.capturedUtc)
                && a.capturedUtc == b.capturedUtc;
        }

        // ---------------------------------------------------------------------
        // 指标
        // ---------------------------------------------------------------------

        static void DiffMetrics(PerfSnapshot baseline, PerfSnapshot current, PerfDiffResult result)
        {
            var seen = new HashSet<string>();

            for (int i = 0; i < current.metrics.Count; i++)
            {
                var c = current.metrics[i];
                if (string.IsNullOrEmpty(c.name) || !seen.Add(c.name)) continue;

                var b = baseline.FindMetric(c.name);
                var d = new MetricDelta();
                d.name = c.name;
                d.unit = c.unit;
                d.current = c.value;
                d.currentSeverity = c.severity;

                if (b == null)
                {
                    d.baseline = double.NaN;
                    d.baselineSeverity = Severity.Info;
                    d.onlyInCurrent = true;
                    d.direction = DirectionOf(c.name, c);
                    result.metrics.Add(d);
                    continue;
                }

                d.baseline = b.value;
                d.baselineSeverity = b.severity;
                d.delta = c.value - b.value;
                d.deltaPercent = Math.Abs(b.value) < 1e-12 ? double.NaN : (d.delta / Math.Abs(b.value)) * 100.0;
                d.direction = DirectionOf(c.name, c);

                bool significant = Math.Abs(b.value) < 1e-12
                    ? Math.Abs(d.delta) > 1e-9
                    : Math.Abs(d.delta) / Math.Abs(b.value) > SignificanceThreshold;

                if (significant && d.direction != DiffDirection.Neutral)
                {
                    bool worse = d.direction == DiffDirection.Cost ? d.delta > 0 : d.delta < 0;
                    d.changed = true;
                    d.regressed = worse;
                    d.improved = !worse;
                }

                result.metrics.Add(d);
            }

            for (int i = 0; i < baseline.metrics.Count; i++)
            {
                var b = baseline.metrics[i];
                if (string.IsNullOrEmpty(b.name) || seen.Contains(b.name)) continue;
                if (current.FindMetric(b.name) != null) continue;

                var d = new MetricDelta();
                d.name = b.name;
                d.unit = b.unit;
                d.baseline = b.value;
                d.baselineSeverity = b.severity;
                d.onlyInBaseline = true;
                d.direction = DirectionOf(b.name, b);
                result.metrics.Add(d);
            }
        }

        /// <summary>
        /// 判定指标方向。优先看是否带预算（带预算 = 有明确上限 = 越小越好），
        /// 其次看命名关键词；两者都无法判定时返回 Neutral 而不是猜。
        /// </summary>
        static string DirectionOf(string name, PerfMetric metric)
        {
            for (int i = 0; i < BenefitKeywords.Length; i++)
                if (name.IndexOf(BenefitKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return DiffDirection.Benefit;

            bool hasBudget = metric != null
                && ((!string.IsNullOrEmpty(metric.budget) && metric.budget != "0") || !string.IsNullOrEmpty(metric.budgetUnit));
            if (hasBudget) return DiffDirection.Cost;

            for (int i = 0; i < CostKeywords.Length; i++)
                if (name.IndexOf(CostKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return DiffDirection.Cost;

            return DiffDirection.Neutral;
        }

        // ---------------------------------------------------------------------
        // 结论
        // ---------------------------------------------------------------------

        static void DiffFindings(PerfSnapshot baseline, PerfSnapshot current, PerfDiffResult result)
        {
            var baselineMap = new Dictionary<string, PerfFinding>();
            for (int i = 0; i < baseline.findings.Count; i++)
                baselineMap[Key(baseline.findings[i])] = baseline.findings[i];

            var currentKeys = new HashSet<string>();
            for (int i = 0; i < current.findings.Count; i++)
            {
                var c = current.findings[i];
                string key = Key(c);
                currentKeys.Add(key);

                PerfFinding b;
                if (!baselineMap.TryGetValue(key, out b))
                {
                    result.newFindings.Add(c);
                    continue;
                }

                int br = Severity.Rank(b.severity);
                int cr = Severity.Rank(c.severity);
                if (cr > br) result.worsenedFindings.Add(c);
                else if (cr < br) result.improvedFindings.Add(c);
                else result.persistingFindings.Add(c);
            }

            for (int i = 0; i < baseline.findings.Count; i++)
            {
                var b = baseline.findings[i];
                if (!currentKeys.Contains(Key(b))) result.resolvedFindings.Add(b);
            }
        }

        static string Key(PerfFinding f)
        {
            if (f == null) return "";
            return string.IsNullOrEmpty(f.id) ? f.title : f.id;
        }

        // ---------------------------------------------------------------------
        // 资源 / 场景 / 代码问题
        // ---------------------------------------------------------------------

        static void DiffIssues(PerfSnapshot baseline, PerfSnapshot current, PerfDiffResult result)
        {
            var baseAssets = new HashSet<string>();
            for (int i = 0; i < baseline.assetIssues.Count; i++) baseAssets.Add(AssetKey(baseline.assetIssues[i]));
            var curAssets = new HashSet<string>();
            for (int i = 0; i < current.assetIssues.Count; i++)
            {
                var a = current.assetIssues[i];
                if (curAssets.Add(AssetKey(a)) && !baseAssets.Contains(AssetKey(a))) result.newAssetIssues.Add(a);
            }
            for (int i = 0; i < baseline.assetIssues.Count; i++)
            {
                var a = baseline.assetIssues[i];
                if (!curAssets.Contains(AssetKey(a))) result.resolvedAssetIssues.Add(a);
            }

            var baseScenes = new HashSet<string>();
            for (int i = 0; i < baseline.sceneIssues.Count; i++) baseScenes.Add(SceneKey(baseline.sceneIssues[i]));
            var curScenes = new HashSet<string>();
            for (int i = 0; i < current.sceneIssues.Count; i++)
            {
                var a = current.sceneIssues[i];
                if (curScenes.Add(SceneKey(a)) && !baseScenes.Contains(SceneKey(a))) result.newSceneIssues.Add(a);
            }
            for (int i = 0; i < baseline.sceneIssues.Count; i++)
            {
                var a = baseline.sceneIssues[i];
                if (!curScenes.Contains(SceneKey(a))) result.resolvedSceneIssues.Add(a);
            }

            var baseCode = new HashSet<string>();
            for (int i = 0; i < baseline.codeIssues.Count; i++) baseCode.Add(CodeKey(baseline.codeIssues[i]));
            var curCode = new HashSet<string>();
            for (int i = 0; i < current.codeIssues.Count; i++)
            {
                var a = current.codeIssues[i];
                if (curCode.Add(CodeKey(a)) && !baseCode.Contains(CodeKey(a))) result.newCodeIssues.Add(a);
            }
            for (int i = 0; i < baseline.codeIssues.Count; i++)
            {
                var a = baseline.codeIssues[i];
                if (!curCode.Contains(CodeKey(a))) result.resolvedCodeIssues.Add(a);
            }
        }

        static string AssetKey(AssetIssue a) { return a.path + "|" + a.issue; }
        static string SceneKey(SceneIssue a) { return a.hierarchyPath + "|" + a.componentType + "|" + a.issue; }
        static string CodeKey(CodeIssue a) { return a.file + ":" + a.line + "|" + a.pattern; }
    }
}

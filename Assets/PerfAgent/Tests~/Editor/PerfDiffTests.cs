using NUnit.Framework;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.Tests
{
    /// <summary>
    /// 会话对比（P4）回归：验证「变大是好是坏」这件事由方向规则决定，
    /// 而不是留给模型或用户自行推断，并且噪声不会被误报成回归。
    /// </summary>
    public class PerfDiffTests
    {
        [Test]
        public void SameSnapshotPair_IsReportedAsNotComparable()
        {
            var snapshot = Snapshot("s1", "2026-01-01T00:00:00Z");
            snapshot.SetMetric("Draw Calls", "次", 200);

            var diff = PerfDiff.Compare(snapshot, snapshot);

            Assert.IsFalse(diff.Comparable);
            Assert.IsNotEmpty(diff.error);
            Assert.AreEqual("不可对比", diff.Verdict());
        }

        [Test]
        public void CostAndBenefitMetrics_AreBothJudgedInTheCorrectDirection()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.SetMetric("Draw Calls", "次", 200);
            baseline.SetMetric("实际 FPS", "fps", 60);
            baseline.SetMetric("每帧托管分配", "B", 0);
            MarkBudgeted(baseline, "Draw Calls", "300");
            MarkBudgeted(baseline, "每帧托管分配", "2048");

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.SetMetric("Draw Calls", "次", 800);
            current.SetMetric("实际 FPS", "fps", 30);
            current.SetMetric("每帧托管分配", "B", 4096);
            MarkBudgeted(current, "Draw Calls", "300");
            MarkBudgeted(current, "每帧托管分配", "2048");

            var diff = PerfDiff.Compare(baseline, current);

            Assert.IsTrue(diff.Comparable);
            // Draw Call 变大 = 变差；FPS 变小 = 变差；每帧分配从 0 变正 = 变差
            Assert.AreEqual(3, diff.RegressedMetricCount, "三项都应判定为恶化");
            Assert.AreEqual(0, diff.ImprovedMetricCount);
            Assert.AreEqual(DiffDirection.Cost, Find(diff, "Draw Calls").direction);
            Assert.AreEqual(DiffDirection.Benefit, Find(diff, "实际 FPS").direction);
            Assert.IsTrue(Find(diff, "实际 FPS").regressed, "FPS 下降必须判定为恶化，而不是只看正负号");
            StringAssert.Contains("恶化", diff.Verdict());
        }

        [Test]
        public void Improvement_IsDetectedAsReduced()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.SetMetric("Draw Calls 均值", "次", 900);
            baseline.SetMetric("实际 FPS", "fps", 30);

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.SetMetric("Draw Calls 均值", "次", 220);
            current.SetMetric("实际 FPS", "fps", 58);

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(0, diff.RegressedMetricCount);
            Assert.AreEqual(2, diff.ImprovedMetricCount);
            StringAssert.Contains("改善", diff.Verdict());
        }

        [Test]
        public void SubThresholdNoise_IsNotReportedAsRegression()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.SetMetric("帧耗时均值", "ms", 100);

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.SetMetric("帧耗时均值", "ms", 101);   // +1%，低于 2% 显著性阈值

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(0, diff.RegressedMetricCount);
            Assert.AreEqual(0, diff.SignificantlyChangedCount);
            Assert.IsFalse(Find(diff, "帧耗时均值").changed);
            Assert.AreEqual("基本持平", diff.Verdict());
        }

        [Test]
        public void NeutralMetrics_AreNotJudgedEitherWay()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.SetMetric("资源总数", "个", 100);

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.SetMetric("资源总数", "个", 400);

            var diff = PerfDiff.Compare(baseline, current);

            var metric = Find(diff, "资源总数");
            Assert.AreEqual(DiffDirection.Neutral, metric.direction);
            Assert.IsFalse(metric.changed, "没有预算也没有成本关键词的指标不应被判定为恶化");
            Assert.AreEqual(0, diff.RegressedMetricCount);
        }

        [Test]
        public void Findings_AreSplitIntoNewPersistingAndResolved()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.findings.Add(Finding("gc_alloc_per_frame", Severity.Error));
            baseline.findings.Add(Finding("draw_calls_over", Severity.Warn));

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.findings.Add(Finding("draw_calls_over", Severity.Warn));
            current.findings.Add(Finding("texture_memory_over", Severity.Warn));

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(1, diff.newFindings.Count);
            Assert.AreEqual("texture_memory_over", diff.newFindings[0].id);
            Assert.AreEqual(1, diff.resolvedFindings.Count);
            Assert.AreEqual("gc_alloc_per_frame", diff.resolvedFindings[0].id);
            Assert.AreEqual(1, diff.persistingFindings.Count);
        }

        [Test]
        public void FindingSeverityChange_IsReportedAsWorseOrBetter()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.findings.Add(Finding("gc_alloc_per_frame", Severity.Warn));

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.findings.Add(Finding("gc_alloc_per_frame", Severity.Error));

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(1, diff.worsenedFindings.Count);
            Assert.AreEqual(0, diff.newFindings.Count);
            Assert.AreEqual(0, diff.resolvedFindings.Count);
            Assert.Greater(diff.Score(), 0, "结论加重应让加权分为正");
        }

        [Test]
        public void ResolvedErrors_PullTheVerdictTowardsImprovement()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.findings.Add(Finding("gc_alloc_per_frame", Severity.Error));
            baseline.findings.Add(Finding("definitely_out_of_budget", Severity.Error));

            var current = Snapshot("now", "2026-01-02T00:00:00Z");

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(2, diff.resolvedFindings.Count);
            Assert.Less(diff.Score(), 0);
            StringAssert.Contains("改善", diff.Verdict());
        }

        [Test]
        public void IssueLists_AreDiffedByStableIdentity()
        {
            var baseline = Snapshot("base", "2026-01-01T00:00:00Z");
            baseline.assetIssues.Add(new AssetIssue { path = "Assets/A.png", issue = "Read/Write 已开启", severity = Severity.Warn });
            baseline.sceneIssues.Add(new SceneIssue { hierarchyPath = "Root/A", componentType = "Camera", issue = "相机数量过多", severity = Severity.Warn });
            baseline.codeIssues.Add(new CodeIssue { file = "Assets/A.cs", line = 10, pattern = "Camera.main", severity = Severity.Warn });

            var current = Snapshot("now", "2026-01-02T00:00:00Z");
            current.assetIssues.Add(new AssetIssue { path = "Assets/B.png", issue = "Read/Write 已开启", severity = Severity.Warn });
            current.codeIssues.Add(new CodeIssue { file = "Assets/A.cs", line = 10, pattern = "Camera.main", severity = Severity.Warn });

            var diff = PerfDiff.Compare(baseline, current);

            Assert.AreEqual(1, diff.newAssetIssues.Count);
            Assert.AreEqual("Assets/B.png", diff.newAssetIssues[0].path);
            Assert.AreEqual(1, diff.resolvedAssetIssues.Count);
            Assert.AreEqual("Assets/A.png", diff.resolvedAssetIssues[0].path);
            Assert.AreEqual(1, diff.resolvedSceneIssues.Count);
            Assert.AreEqual(0, diff.newCodeIssues.Count);
            Assert.AreEqual(0, diff.resolvedCodeIssues.Count);
        }

        [Test]
        public void Markdown_ReportsVerdictAndCounts()
        {
            var baseline = Snapshot("base-id", "2026-01-01T00:00:00Z");
            baseline.SetMetric("Draw Calls", "次", 900);
            var current = Snapshot("now-id", "2026-01-02T00:00:00Z");
            current.SetMetric("Draw Calls", "次", 200);

            var diff = PerfDiff.Compare(baseline, current);
            var markdown = diff.ToMarkdown();

            StringAssert.Contains("base-id", markdown);
            StringAssert.Contains("now-id", markdown);
            StringAssert.Contains("改善", markdown);
            StringAssert.Contains("Draw Calls", markdown);
        }

        // ---------------------------------------------------------------------

        static MetricDelta Find(PerfDiffResult diff, string name)
        {
            for (int i = 0; i < diff.metrics.Count; i++)
                if (diff.metrics[i].name == name) return diff.metrics[i];

            Assert.Fail("对比结果里没有指标：" + name);
            return null;
        }

        static void MarkBudgeted(PerfSnapshot snapshot, string metric, string budget)
        {
            var m = snapshot.FindMetric(metric);
            m.budget = budget;
            m.budgetUnit = m.unit;
        }

        static PerfFinding Finding(string id, string severity)
        {
            return new PerfFinding
            {
                id = id,
                category = "内存",
                severity = severity,
                title = "结论 " + id,
                recommendation = "修复建议"
            };
        }

        static PerfSnapshot Snapshot(string id, string capturedUtc)
        {
            return new PerfSnapshot { id = id, label = id, capturedUtc = capturedUtc };
        }
    }
}

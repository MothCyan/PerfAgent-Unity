using NUnit.Framework;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.Tests
{
    /// <summary>
    /// 一键修复计划（P4）回归。
    ///
    /// 这里验证的不是「能不能执行」，而是**计划的正确性与安全性**：
    /// 风险分级不能错、没有执行器的结论绝不允许出现执行按钮、
    /// 高风险动作必须附带人工确认步骤。
    /// </summary>
    public class PerfFixPlanTests
    {
        static readonly PerfBudget Budget = new PerfBudget
        {
            maxManagedAllocBytesPerFrame = 2048,
            maxDrawCalls = 300,
            maxTextureMemoryMB = 128,
            maxTempAllocatorMB = 64,
            maxMainThreadMs = 10
        };

        [Test]
        public void TextureReadWrite_OffersSafeReversibleBatchFix()
        {
            var snapshot = Clean();
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/B.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_texture_readwrite");

            Assert.IsNotNull(plan, "纹理 Read/Write 结论应产出修复计划");
            Assert.IsTrue(plan.HasExecutable);

            var step = plan.steps[0];
            Assert.AreEqual(FixActionIds.TextureReadWriteOff, step.actionId);
            Assert.AreEqual(FixRisk.Safe, step.risk);
            Assert.AreEqual(2, step.targetCount);
            Assert.IsTrue(step.reversible, "导入设置类修复必须可撤销");
        }

        [Test]
        public void SameIssueWithDifferentNumbers_GroupsIntoOneFinding()
        {
            var snapshot = Clean();
            // 同一类问题、文案里的数值不同，必须归到同一条结论里
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_uncompressed", "未压缩纹理（1024x1024）", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/B.png", "texture_uncompressed", "未压缩纹理（2048x2048）", Severity.Error));
            Evaluate(snapshot);

            int count = 0;
            for (int i = 0; i < snapshot.findings.Count; i++)
                if (snapshot.findings[i].fixCode == "texture_uncompressed") count++;

            Assert.AreEqual(1, count, "同一 code 的不同文案不应被拆成多条结论");
        }

        [Test]
        public void ModelColliderFix_IsRiskyAndComesWithManualStep()
        {
            var snapshot = Clean();
            snapshot.assetIssues.Add(Asset("Assets/Char.fbx", "model_auto_collider", "自动生成碰撞体", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_model_auto_collider");

            Assert.IsNotNull(plan);
            Assert.AreEqual(FixRisk.Risky, plan.steps[0].risk, "关掉碰撞体属于高风险");
            Assert.IsTrue(HasManualStep(plan), "高风险动作必须配人工确认步骤");
        }

        [Test]
        public void GcAllocFinding_IsNotPretendedToBeOneClickFixable()
        {
            var snapshot = Clean();
            snapshot.SetMetric("每帧托管分配", "B", 64 * 1024, "gold/gc-alloc");
            snapshot.codeIssues.Add(new CodeIssue
            {
                file = "Assets/A.cs",
                line = 42,
                pattern = "linq",
                snippet = "list.Where(...)",
                severity = Severity.Warn
            });
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "gc_alloc_per_frame");

            Assert.IsNotNull(plan);
            Assert.IsTrue(HasNavigateStep(plan), "代码类结论应提供跳转到源码的步骤");
            Assert.IsFalse(plan.HasExecutable, "代码级每帧分配不存在一键修复，不能给出执行按钮");
        }

        [Test]
        public void UnknownIssueCode_ProducesNoExecuteButton()
        {
            var snapshot = Clean();
            snapshot.assetIssues.Add(Asset("Assets/Weird.asset", "some_future_code", "未知问题", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_some_future_code");

            Assert.IsNotNull(plan, "没有执行器的结论也要有兜底的人工计划");
            Assert.IsFalse(plan.HasExecutable, "没有执行器时绝不能给出执行按钮");
            Assert.IsTrue(HasManualStep(plan));
        }

        [Test]
        public void TextureMemoryOver_ExpandsIntoAllTextureFixes()
        {
            var snapshot = Clean();
            snapshot.SetMetric("纹理内存(估算)", "B", 256L * 1024L * 1024L, "gold/texture-memory");
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/B.png", "texture_uncompressed", "未压缩纹理", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/C.png", "texture_max_size", "maxTextureSize 偏大", Severity.Warn));
            snapshot.assetIssues.Add(Asset("Assets/D.png", "texture_no_streaming", "未开启 Mipmap Streaming", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "texture_memory_over");

            Assert.IsNotNull(plan);
            Assert.AreEqual(4, plan.ExecutableStepCount, "四类纹理问题都应给出可执行步骤");
        }

        [Test]
        public void Plans_AreSortedBySeverityThenExecutability()
        {
            var snapshot = Clean();
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            snapshot.codeIssues.Add(new CodeIssue { file = "Assets/B.cs", line = 3, pattern = "linq", severity = Severity.Info });
            Evaluate(snapshot);

            var plans = PerfFixPlanner.BuildPlans(snapshot);

            Assert.Greater(plans.Count, 1);
            Assert.AreEqual(Severity.Error, plans[0].severity, "error 级结论必须排在前面");
        }

        // ---------------------------------------------------------------------

        static PerfFixPlan PlanFor(PerfSnapshot snapshot, string findingId)
        {
            var plans = PerfFixPlanner.BuildPlans(snapshot);
            for (int i = 0; i < plans.Count; i++)
                if (plans[i].findingId == findingId) return plans[i];
            return null;
        }

        static bool HasManualStep(PerfFixPlan plan)
        {
            for (int i = 0; i < plan.steps.Count; i++)
                if (!plan.steps[i].CanExecute) return true;
            return false;
        }

        static bool HasNavigateStep(PerfFixPlan plan)
        {
            for (int i = 0; i < plan.steps.Count; i++)
                if (plan.steps[i].kind == FixKind.Navigate) return true;
            return false;
        }

        static void Evaluate(PerfSnapshot snapshot)
        {
            new PerfRuleEngine().Evaluate(snapshot, Budget);
        }

        static AssetIssue Asset(string path, string code, string issue, string severity)
        {
            var a = new AssetIssue();
            a.path = path;
            a.code = code;
            a.issue = issue;
            a.assetType = "Texture";
            a.severity = severity;
            return a;
        }

        static PerfSnapshot Clean()
        {
            return new PerfSnapshot
            {
                id = "fix-clean",
                label = "Fix Plan",
                targetFrameRate = 60,
                vSyncCount = 1,
                fixedDeltaTime = 0.02f,
                physicsSimulationMode = "FixedUpdate"
            };
        }
    }
}

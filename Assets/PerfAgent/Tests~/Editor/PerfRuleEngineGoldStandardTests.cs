using System.Collections.Generic;
using NUnit.Framework;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.Tests
{
    /// <summary>
    /// P5 gold-standard 回归集。每个用例只注入一种故障，验证规则引擎能把它
    /// 定位到稳定的规则 ID/模块，并且结论始终携带可回溯证据。
    /// </summary>
    public class PerfRuleEngineGoldStandardTests
    {
        PerfBudget _budget;

        [SetUp]
        public void SetUp()
        {
            _budget = new PerfBudget
            {
                maxManagedAllocBytesPerFrame = 2048,
                maxDrawCalls = 300,
                maxTextureMemoryMB = 128,
                maxTempAllocatorMB = 64,
                maxMainThreadMs = 10
            };
        }

        [Test]
        public void GcAllocExplosion_IsLocatedInMemoryModule()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("每帧托管分配", "B", 64 * 1024, "gold/gc-alloc");

            AssertFinding(snapshot, "gc_alloc_per_frame", "内存", Severity.Error);
        }

        [Test]
        public void ExcessiveDrawCalls_AreLocatedInRenderingModule()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("Draw Calls", "次", 800, "gold/draw-calls");

            AssertFinding(snapshot, "draw_calls_over", "渲染", Severity.Error);
        }

        [Test]
        public void TextureMemoryLeak_IsLocatedInMemoryModule()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("纹理内存(估算)", "B", 256L * 1024L * 1024L, "gold/texture-memory");
            snapshot.assetIssues.Add(new AssetIssue
            {
                path = "Assets/Gold/LeakingTexture.png",
                assetType = "Texture",
                estimatedMemoryBytes = 256L * 1024L * 1024L,
                issue = "纹理 Read/Write 已开启",
                suggestion = "关闭 Read/Write",
                severity = Severity.Error
            });

            AssertFinding(snapshot, "texture_memory_over", "内存", Severity.Error);
        }

        [Test]
        public void TempAllocatorGrowth_IsLocatedInMemoryModule()
        {
            var snapshot = CleanSnapshot();
            const long start = 8L * 1024L * 1024L;
            for (var i = 0; i < 60; i++)
            {
                snapshot.frames.Add(new FrameStat
                {
                    frame = i,
                    deltaMs = 8,
                    tempAllocBytes = start + i * 512L * 1024L
                });
            }

            AssertFinding(snapshot, "temp_allocator_growth", "内存", Severity.Warn);
        }

        [Test]
        public void InvalidPhysicsConfiguration_IsLocatedInPhysicsModule()
        {
            var snapshot = CleanSnapshot();
            snapshot.physicsAutoSyncTransforms = true;
            snapshot.sceneIssues.Add(new SceneIssue
            {
                hierarchyPath = "(场景级)",
                componentType = "Physics",
                issue = "Physics.autoSyncTransforms = true，Transform 变更会立即同步物理",
                suggestion = "关闭 Auto Sync Transforms",
                severity = Severity.Error
            });

            var finding = AssertSingleMatching(snapshot, f =>
                f.category == "场景" && f.title.Contains("autoSyncTransforms"));
            Assert.AreEqual(Severity.Error, finding.severity);
            AssertEvidence(finding);
        }

        [Test]
        public void CleanSnapshot_DoesNotProduceFiveFaultSignatures()
        {
            var findings = Evaluate(CleanSnapshot());
            var forbidden = new HashSet<string>
            {
                "gc_alloc_per_frame",
                "draw_calls_over",
                "texture_memory_over",
                "temp_allocator_growth"
            };

            Assert.IsFalse(findings.Exists(f => forbidden.Contains(f.id)));
            Assert.IsFalse(findings.Exists(f => f.title.Contains("autoSyncTransforms")));
        }

        PerfFinding AssertFinding(PerfSnapshot snapshot, string id, string category, string severity)
        {
            var finding = AssertSingleMatching(snapshot, f => f.id == id);
            Assert.AreEqual(category, finding.category);
            Assert.AreEqual(severity, finding.severity);
            AssertEvidence(finding);
            return finding;
        }

        PerfFinding AssertSingleMatching(PerfSnapshot snapshot, System.Predicate<PerfFinding> predicate)
        {
            var matches = Evaluate(snapshot).FindAll(predicate);
            Assert.AreEqual(1, matches.Count, "gold-standard 故障应且仅应命中一个目标结论");
            return matches[0];
        }

        static void AssertEvidence(PerfFinding finding)
        {
            Assert.IsNotNull(finding.evidence);
            Assert.IsNotEmpty(finding.evidence, "结论必须携带证据链");
            Assert.IsTrue(finding.evidence.TrueForAll(e =>
                !string.IsNullOrEmpty(e.tool) && !string.IsNullOrEmpty(e.metric)));
        }

        List<PerfFinding> Evaluate(PerfSnapshot snapshot)
        {
            return new PerfRuleEngine().Evaluate(snapshot, _budget);
        }

        static PerfSnapshot CleanSnapshot()
        {
            return new PerfSnapshot
            {
                id = "gold-clean",
                label = "P5 Gold Standard",
                targetFrameRate = 60,
                vSyncCount = 1,
                fixedDeltaTime = 0.02f,
                physicsSimulationMode = "FixedUpdate"
            };
        }
    }
}
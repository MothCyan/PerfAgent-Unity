using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Agent;
using PerfAgent.Analysis;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    static class Program
    {
        static readonly PerfBudget Budget = new PerfBudget
        {
            maxManagedAllocBytesPerFrame = 2048,
            maxDrawCalls = 300,
            maxTextureMemoryMB = 128,
            maxTempAllocatorMB = 64,
            maxMainThreadMs = 10
        };

        static int Main()
        {
            var tests = new Action[]
            {
                GcAllocExplosion,
                ExcessiveDrawCalls,
                TextureMemoryLeak,
                TempAllocatorGrowth,
                InvalidPhysicsConfiguration,
                CleanSnapshotDoesNotMatchFaults,
                DiffRejectsSameSnapshot,
                DiffClassifiesMetricDirection,
                DiffIgnoresSubThresholdNoise,
                DiffSeparatesNewAndResolvedFindings,
                DiffWeightsResolvedErrorsAsImprovement,
                FixPlanTextureReadWriteIsSafeAndExecutable,
                FixPlanModelColliderIsRisky,
                FixPlanSceneAutoSyncIsProjectSetting,
                FixPlanGcAllocHasNavigationAndManualSteps,
                FixPlanTextureMemoryExpandsAllTextureFixes,
                FixPlanUnknownCodeHasNoExecutableStep,
                RecorderGcAllocIsPreferredWhenAvailable,
                MissingRecorderGcAllocDegradesExplicitly,
                EndpointUrlIsNormalizedBeforeRequest,
                PlayModeTestJobRoundTripsThroughJson,
                PlayModeTestJobClassifiesPhases,
                PlayModeTestJobProgressReflectsPhase,
                PlayModeTestSetupMethodIsParsedSafely,
                PlayModeTestEventLogIsBounded,
                PlayModeTestUnknownPhaseFallsBackToNone,
                SceneFindingsHaveExecutableFixes,
                DrawCallFindingOffersAtlasFix,
                CodeFindingsExposeSafeAutoFixes,
                SourcePatcherOnlyRewritesSafeLines,
                FrameDownsamplePreservesShapeAndBounds,
                PlayModeTestJobProgressHandlesDurationMode,
                ToolResultFolderFoldsWithoutBreakingPairs,
                CodeFixParserExtractsSuggestion,
                CodePatternIsParsedBeforeMatching
            };

            var failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + exception.Message);
                }
            }

            Console.WriteLine("Result: " + (tests.Length - failed) + "/" + tests.Length + " passed");
            return failed == 0 ? 0 : 1;
        }

        static void GcAllocExplosion()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("每帧托管分配", "B", 64 * 1024, "gold/gc-alloc");
            AssertFinding(snapshot, "gc_alloc_per_frame", "内存", Severity.Error);
        }

        static void ExcessiveDrawCalls()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("Draw Calls", "次", 800, "gold/draw-calls");
            AssertFinding(snapshot, "draw_calls_over", "渲染", Severity.Error);
        }

        static void TextureMemoryLeak()
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

        static void TempAllocatorGrowth()
        {
            var snapshot = CleanSnapshot();
            const long start = 8L * 1024L * 1024L;
            for (var i = 0; i < 60; i++)
                snapshot.frames.Add(new FrameStat { frame = i, deltaMs = 8, tempAllocBytes = start + i * 512L * 1024L });
            AssertFinding(snapshot, "temp_allocator_growth", "内存", Severity.Warn);
        }

        static void InvalidPhysicsConfiguration()
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
            var finding = Single(snapshot, f => f.category == "场景" && f.title.Contains("autoSyncTransforms"));
            Equal(Severity.Error, finding.severity, "physics severity");
            Evidence(finding);
        }

        static void CleanSnapshotDoesNotMatchFaults()
        {
            var findings = Evaluate(CleanSnapshot());
            var ids = new HashSet<string> { "gc_alloc_per_frame", "draw_calls_over", "texture_memory_over", "temp_allocator_growth" };
            True(!findings.Exists(f => ids.Contains(f.id)), "clean snapshot matched a fault rule");
            True(!findings.Exists(f => f.title.Contains("autoSyncTransforms")), "clean snapshot matched physics fault");
        }

        // =====================================================================
        // 会话对比（P4）：方向判定必须由规则给出，噪声不得被误报为回归
        // =====================================================================

        static void DiffRejectsSameSnapshot()
        {
            var snapshot = DiffSnapshot("s1", "t1");
            snapshot.SetMetric("Draw Calls", "次", 200);

            var diff = PerfDiff.Compare(snapshot, snapshot);

            True(!diff.Comparable, "same snapshot pair must not be comparable");
            True(!string.IsNullOrEmpty(diff.error), "not comparable diff must explain why");
        }

        static void DiffClassifiesMetricDirection()
        {
            var baseline = DiffSnapshot("base", "t1");
            baseline.SetMetric("Draw Calls", "次", 200);
            baseline.SetMetric("实际 FPS", "fps", 60);
            baseline.SetMetric("每帧托管分配", "B", 0);
            Budgeted(baseline, "Draw Calls", "300");
            Budgeted(baseline, "每帧托管分配", "2048");

            var current = DiffSnapshot("now", "t2");
            current.SetMetric("Draw Calls", "次", 800);
            current.SetMetric("实际 FPS", "fps", 30);
            current.SetMetric("每帧托管分配", "B", 4096);
            Budgeted(current, "Draw Calls", "300");
            Budgeted(current, "每帧托管分配", "2048");

            var diff = PerfDiff.Compare(baseline, current);

            True(diff.Comparable, "comparable pair must be comparable");
            Equal(3, diff.RegressedMetricCount, "regressed metric count");
            Equal(0, diff.ImprovedMetricCount, "improved metric count");
            Equal(DiffDirection.Cost, Metric(diff, "Draw Calls").direction, "draw calls direction");
            Equal(DiffDirection.Benefit, Metric(diff, "实际 FPS").direction, "fps direction");
            True(Metric(diff, "实际 FPS").regressed, "fps drop must be a regression, not just a sign change");
            True(diff.Verdict().Contains("恶化"), "verdict must report regression, got " + diff.Verdict());
        }

        static void DiffIgnoresSubThresholdNoise()
        {
            var baseline = DiffSnapshot("base", "t1");
            baseline.SetMetric("帧耗时均值", "ms", 100);

            var current = DiffSnapshot("now", "t2");
            current.SetMetric("帧耗时均值", "ms", 101);   // +1%，低于 2% 显著性阈值

            var diff = PerfDiff.Compare(baseline, current);
            Equal(0, diff.SignificantlyChangedCount, "sub-threshold noise count");
            Equal("基本持平", diff.Verdict(), "verdict");
        }

        static void DiffSeparatesNewAndResolvedFindings()
        {
            var baseline = DiffSnapshot("base", "t1");
            baseline.findings.Add(DiffFinding("gc_alloc_per_frame", Severity.Error));
            baseline.findings.Add(DiffFinding("draw_calls_over", Severity.Warn));

            var current = DiffSnapshot("now", "t2");
            current.findings.Add(DiffFinding("draw_calls_over", Severity.Warn));
            current.findings.Add(DiffFinding("texture_memory_over", Severity.Warn));

            var diff = PerfDiff.Compare(baseline, current);
            Equal(1, diff.newFindings.Count, "new findings");
            Equal("texture_memory_over", diff.newFindings[0].id, "new finding id");
            Equal(1, diff.resolvedFindings.Count, "resolved findings");
            Equal("gc_alloc_per_frame", diff.resolvedFindings[0].id, "resolved finding id");
            Equal(1, diff.persistingFindings.Count, "persisting findings");
            True(diff.ToMarkdown().Contains("快照对比"), "markdown must contain the diff section");
        }

        static void DiffWeightsResolvedErrorsAsImprovement()
        {
            var baseline = DiffSnapshot("base", "t1");
            baseline.findings.Add(DiffFinding("gc_alloc_per_frame", Severity.Error));
            baseline.findings.Add(DiffFinding("texture_memory_over", Severity.Error));

            var current = DiffSnapshot("now", "t2");
            var diff = PerfDiff.Compare(baseline, current);

            Equal(2, diff.resolvedFindings.Count, "resolved findings");
            True(diff.Score() < 0, "score must be negative, got " + diff.Score());
            True(diff.Verdict().Contains("改善"), "verdict must report improvement, got " + diff.Verdict());
        }

        // =====================================================================
        // 一键修复计划（P4）
        // 验证点：计划必须来自确定性映射，风险分级不能错，没有执行器的绝不能给出执行按钮
        // =====================================================================

        static void FixPlanTextureReadWriteIsSafeAndExecutable()
        {
            var snapshot = CleanSnapshot();
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/B.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_texture_readwrite");
            True(plan != null, "should build a plan for texture read/write");
            True(plan.HasExecutable, "texture read/write must be executable");

            var step = plan.steps[0];
            True(step.CanExecute, "step must be executable");
            Equal(FixActionIds.TextureReadWriteOff, step.actionId, "action id");
            Equal(FixRisk.Safe, step.risk, "risk");
            Equal(2, step.targetCount, "target count");
            True(step.reversible, "import setting fix must be reversible");
        }

        static void FixPlanModelColliderIsRisky()
        {
            var snapshot = CleanSnapshot();
            snapshot.assetIssues.Add(Asset("Assets/Char.fbx", "model_auto_collider", "自动生成碰撞体", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_model_auto_collider");
            True(plan != null, "should build a plan for model auto collider");

            var step = plan.steps[0];
            Equal(FixRisk.Risky, step.risk, "removing colliders must be risky");
            Equal(FixActionIds.ModelColliderOff, step.actionId, "action id");
            // 高风险动作必须附带人工确认步骤
            True(HasManualStep(plan), "risky fix must ship with a manual follow-up step");
        }

        static void FixPlanSceneAutoSyncIsProjectSetting()
        {
            var snapshot = CleanSnapshot();
            snapshot.physicsAutoSyncTransforms = true;
            snapshot.sceneIssues.Add(new SceneIssue
            {
                code = "scene_autosync_transforms",
                hierarchyPath = "(场景级)",
                componentType = "Physics",
                issue = "Physics.autoSyncTransforms = true",
                severity = Severity.Error
            });
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "scene_scene_autosync_transforms");
            True(plan != null, "should build a plan for autoSyncTransforms");

            var step = plan.steps[0];
            Equal(FixKind.ProjectSetting, step.kind, "kind");
            Equal(FixRisk.Safe, step.risk, "risk");
            Equal(FixActionIds.PhysicsAutoSyncOff, step.actionId, "action id");
            True(step.CanExecute, "project setting fix must be executable");
        }

        static void FixPlanGcAllocHasNavigationAndManualSteps()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("每帧托管分配", "B", 64 * 1024, "gold/gc-alloc");
            snapshot.codeIssues.Add(new CodeIssue
            {
                file = "Assets/A.cs", line = 42, pattern = "linq",
                snippet = "list.Where(...)", severity = Severity.Warn
            });
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "gc_alloc_per_frame");
            True(plan != null, "should build a plan for gc alloc");
            True(HasNavigateStep(plan), "code findings must offer a jump-to-source step");
            True(HasManualStep(plan), "gc alloc must also give manual guidance");
            True(!plan.HasExecutable, "code-level gc alloc must not pretend to be one-click fixable");
        }

        static void FixPlanTextureMemoryExpandsAllTextureFixes()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("纹理内存(估算)", "B", 256L * 1024L * 1024L, "gold/texture-memory");
            snapshot.assetIssues.Add(Asset("Assets/A.png", "texture_readwrite", "开启 Read/Write", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/B.png", "texture_uncompressed", "未压缩纹理", Severity.Error));
            snapshot.assetIssues.Add(Asset("Assets/C.png", "texture_max_size", "maxTextureSize 偏大", Severity.Warn));
            snapshot.assetIssues.Add(Asset("Assets/D.png", "texture_no_streaming", "未开启 Mipmap Streaming", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "texture_memory_over");
            True(plan != null, "should build a plan for texture memory");
            Equal(4, plan.ExecutableStepCount, "all four texture fix kinds must be offered");
        }

        static void FixPlanUnknownCodeHasNoExecutableStep()
        {
            var snapshot = CleanSnapshot();
            snapshot.assetIssues.Add(Asset("Assets/Weird.asset", "some_future_code", "未知问题", Severity.Warn));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "asset_some_future_code");
            True(plan != null, "unknown issue must still produce a plan (manual fallback)");
            True(!plan.HasExecutable, "unknown issue must not get an execute button");
            True(HasManualStep(plan), "unknown issue must fall back to a manual step");
        }

        // =====================================================================
        // Endpoint 归一化
        //
        // 背景：只填域名（如 https://api.deepseek.com）时会 POST 到根路径，
        // 服务端返回 404 且响应体为空，用户完全看不出是自己少写了路径。
        // 下面的用例把常见写法及其预期结果固定下来，避免以后改坏。
        // =====================================================================
        static void EndpointUrlIsNormalizedBeforeRequest()
        {
            // 只填域名：最典型的误用
            Equal("https://api.deepseek.com/v1/chat/completions",
                EndpointUrl.Normalize("https://api.deepseek.com"), "bare host");

            // 带版本段但不带端点
            Equal("https://api.deepseek.com/v1/chat/completions",
                EndpointUrl.Normalize("https://api.deepseek.com/v1"), "version segment only");

            // 尾部斜杠
            Equal("https://api.deepseek.com/v1/chat/completions",
                EndpointUrl.Normalize("https://api.deepseek.com/v1/"), "trailing slash");

            // 首尾空白
            Equal("https://api.deepseek.com/v1/chat/completions",
                EndpointUrl.Normalize("  https://api.deepseek.com/v1  "), "surrounding whitespace");

            // 已经是完整端点：必须原样返回，绝不能又拼一次路径
            Equal("https://api.deepseek.com/v1/chat/completions",
                EndpointUrl.Normalize("https://api.deepseek.com/v1/chat/completions"), "already complete");

            // 非 vN 结尾的版本段（智谱是 /api/paas/v4），不能补成 /v1
            Equal("https://open.bigmodel.cn/api/paas/v4/chat/completions",
                EndpointUrl.Normalize("https://open.bigmodel.cn/api/paas/v4"), "paas style version segment");

            // 阿里通义的兼容模式路径
            Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                EndpointUrl.Normalize("https://dashscope.aliyuncs.com/compatible-mode/v1"), "dashscope compat mode");

            // 本地推理：带端口的裸 host
            Equal("http://localhost:11434/v1/chat/completions",
                EndpointUrl.Normalize("http://localhost:11434"), "local host with port");

            // 空值不应抛异常
            Equal("", EndpointUrl.Normalize(""), "empty string");
            True(EndpointUrl.Normalize(null) == null, "null must pass through unchanged");
        }

        // =====================================================================
        // 自动 Play 模式测试的任务模型
        //
        // 这批用倒重点不是业务逻辑，而是「跨域重载能不能活下来」——
        // 任务状态必须能无损地写成 JSON 再读回来，所以每个字段都要验。
        // =====================================================================
        static void PlayModeTestJobRoundTripsThroughJson()
        {
            var job = new PlayModeTestJob();
            job.id = "PM20260920_101500";
            job.label = "主城跑图";
            job.scenePath = "Assets/Scenes/City.unity";
            job.warmupFrames = 90;
            job.captureFrames = 600;
            job.timeoutSeconds = 300;
            job.setupMethod = "Night.AutoPlay.Start";
            job.restoreScene = false;
            job.phase = PlayModeTestPhase.Capturing;
            job.capturedFrames = 123;
            job.snapshotId = "20260920_101530";
            job.snapshotPath = "C:/p/20260920_101530.json";
            job.previousScenePath = "Assets/Scenes/Main.unity";
            job.AddEvent("已进入 Play 模式");

            var parsed = PlayModeTestJob.Parse(job.ToJson());

            True(parsed != null, "job must survive a json round trip");
            Equal("PM20260920_101500", parsed.id, "id");
            Equal("主城跑图", parsed.label, "label");
            Equal("Assets/Scenes/City.unity", parsed.scenePath, "scene path");
            Equal(90, parsed.warmupFrames, "warmup frames");
            Equal(600, parsed.captureFrames, "capture frames");
            Equal(300.0, parsed.timeoutSeconds, "timeout seconds");
            Equal("Night.AutoPlay.Start", parsed.setupMethod, "setup method");
            True(!parsed.restoreScene, "restore flag must survive");
            True(parsed.phase == PlayModeTestPhase.Capturing, "phase must survive");
            Equal(123, parsed.capturedFrames, "captured frames");
            Equal("20260920_101530", parsed.snapshotId, "snapshot id");
            Equal("C:/p/20260920_101530.json", parsed.snapshotPath, "snapshot path");
            Equal("Assets/Scenes/Main.unity", parsed.previousScenePath, "previous scene");
            Equal(1, parsed.Events.Count, "events must survive");
        }

        static void PlayModeTestJobClassifiesPhases()
        {
            var job = new PlayModeTestJob();

            job.phase = PlayModeTestPhase.Entering;
            True(job.IsActive() && !job.IsTerminal(), "entering is active, not terminal");

            job.phase = PlayModeTestPhase.Warming;
            True(job.IsActive() && !job.IsTerminal(), "warming is active, not terminal");

            job.phase = PlayModeTestPhase.Capturing;
            True(job.IsActive() && !job.IsTerminal(), "capturing is active, not terminal");

            job.phase = PlayModeTestPhase.Finalizing;
            True(job.IsActive() && !job.IsTerminal(), "finalizing is still active");

            job.phase = PlayModeTestPhase.Succeeded;
            True(!job.IsActive() && job.IsTerminal(), "succeeded is terminal");

            job.phase = PlayModeTestPhase.Failed;
            True(!job.IsActive() && job.IsTerminal(), "failed is terminal");

            job.phase = PlayModeTestPhase.Cancelled;
            True(!job.IsActive() && job.IsTerminal(), "cancelled is terminal");

            job.phase = PlayModeTestPhase.None;
            True(!job.IsActive() && !job.IsTerminal(), "none is neither active nor terminal");
        }

        static void PlayModeTestJobProgressReflectsPhase()
        {
            var job = new PlayModeTestJob();
            job.warmupFrames = 100;
            job.captureFrames = 100;

            Equal(0.0, job.Progress(), "not started yet");

            job.phase = PlayModeTestPhase.Warming;
            Equal(0.0, job.Progress(), "warming alone does not advance progress");

            job.phase = PlayModeTestPhase.Capturing;
            job.capturedFrames = 50;
            Equal(0.75, job.Progress(), "warmup + half of capture");

            job.capturedFrames = 100;
            Equal(0.99, job.Progress(), "capturing is capped just below 1");

            job.phase = PlayModeTestPhase.Finalizing;
            Equal(1.0, job.Progress(), "finalizing counts as done");

            job.phase = PlayModeTestPhase.Succeeded;
            Equal(1.0, job.Progress(), "succeeded counts as done");
        }

        static void PlayModeTestSetupMethodIsParsedSafely()
        {
            string type, method;

            True(PlayModeTestJob.SplitSetupMethod("Night.AutoPlay.Start", out type, out method), "plain form");
            Equal("Night.AutoPlay", type, "type from plain form");
            Equal("Start", method, "method from plain form");

            True(PlayModeTestJob.SplitSetupMethod("Night.AutoPlay.Start()", out type, out method), "form with parens");
            Equal("Night.AutoPlay", type, "type from form with parens");
            Equal("Start", method, "method from form with parens");

            True(PlayModeTestJob.SplitSetupMethod("  Game.Boot  ", out type, out method), "surrounding whitespace");
            Equal("Game", type, "trimmed type");
            Equal("Boot", method, "trimmed method");

            // 非法输入必须被拒绕，而不是拼出一个怪类型名再拿去反射
            True(!PlayModeTestJob.SplitSetupMethod("NoDot", out type, out method), "no dot must be rejected");
            True(!PlayModeTestJob.SplitSetupMethod("Trailing.", out type, out method), "trailing dot must be rejected");
            True(!PlayModeTestJob.SplitSetupMethod(".Leading", out type, out method), "leading dot must be rejected");
            True(!PlayModeTestJob.SplitSetupMethod("", out type, out method), "empty must be rejected");
            True(!PlayModeTestJob.SplitSetupMethod(null, out type, out method), "null must be rejected");
        }

        static void PlayModeTestEventLogIsBounded()
        {
            var job = new PlayModeTestJob();
            for (int i = 0; i < 60; i++) job.AddEvent("e" + i);

            Equal(40, job.Events.Count, "event log must be capped so the state file cannot grow forever");
            True(job.Events[job.Events.Count - 1].EndsWith("e59"), "the newest event must be the one kept");
        }

        static void PlayModeTestUnknownPhaseFallsBackToNone()
        {
            // 未来版本可能新增阶段；旧代码读到未知值不能直接崩或者当成进行中
            var job = PlayModeTestJob.Parse("{\"id\":\"x\",\"phase\":\"SomethingNew\"}");
            True(job != null, "job must still parse");
            True(job.phase == PlayModeTestPhase.None, "unknown phase must degrade to None");

            // 缺字段时要落回默认值，而不是 0 —— 否则采样帧数会变成 0 直接卡死流程
            Equal(300, job.captureFrames, "missing capture frames must fall back to default");
            Equal(60, job.warmupFrames, "missing warmup frames must fall back to default");
            True(job.restoreScene, "missing restore flag must fall back to true");
        }

        // =====================================================================
        // 「每种错误都要有自动优化」—— 这条断言就是那个需求的回归锁
        // =====================================================================
        static void SceneFindingsHaveExecutableFixes()
        {
            string[] codes =
            {
                "scene_camera_skybox",
                "scene_light_soft_shadow",
                "scene_particle_max",
                "scene_particle_world_space",
                "scene_physics_simulation_mode",
                "scene_mesh_collider_non_convex",
                "scene_skinned_offscreen",
                "scene_autosync_transforms",
                "scene_fixed_delta_too_small",
            };

            for (int i = 0; i < codes.Length; i++)
            {
                var snapshot = CleanSnapshot();
                snapshot.sceneIssues.Add(SceneIssueOf(codes[i]));
                Evaluate(snapshot);

                // finding id = "scene_" + code（code 本身已带 scene_ 前缀）
                var plan = PlanFor(snapshot, "scene_" + codes[i]);
                True(plan != null, codes[i] + " 应当产出修复计划");
                True(plan.HasExecutable, codes[i] + " 必须有可执行动作，而不是只有人工步骤");
            }
        }

        static void DrawCallFindingOffersAtlasFix()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("Draw Calls", "次", 1500, "gold/draw-calls");
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "draw_calls_over");
            True(plan != null, "Draw Call 超标应当产出计划");
            True(plan.HasExecutable, "应当提供可执行的图集动作，而不只是「建议做图集」");
        }

        static void CodeFindingsExposeSafeAutoFixes()
        {
            var snapshot = CleanSnapshot();
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 10, "tag_compare"));
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 20, "gc_collect"));
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/B.cs", 5, "camera_main"));
            Evaluate(snapshot);

            var tagPlan = PlanFor(snapshot, "code_tag_compare");
            True(tagPlan != null, "tag_compare 应当产出计划");
            True(tagPlan.HasExecutable, "tag_compare 语义等价，应当能自动改写");
            Equal(1, tagPlan.ExecutableStepCount, "tag_compare 只应给一个可执行步骤");
            // 改代码即使语义等价，也不能进「一键执行低风险项」—— 必须由人单独确认
            Equal(0, tagPlan.SafeStepCount, "源码改写不应被标为低风险，否则会被批量执行误伤");

            var gcPlan = PlanFor(snapshot, "code_gc_collect");
            True(gcPlan != null, "gc_collect 应当产出计划");
            True(gcPlan.HasExecutable, "gc_collect 应当能自动改写");

            // 安全边界：需要上下文重构的模式**必须**保持无按钮。
            // 这条断言是故意写死的 —— “补齐功能”的冲动很容易把这条线推过去。
            var cameraPlan = PlanFor(snapshot, "code_camera_main");
            True(cameraPlan != null, "camera_main 应当产出计划（至少给出跳转与建议）");
            True(!cameraPlan.HasExecutable, "camera_main 需要插字段与改初始化时机，不能给执行按钮");
        }

        static void SourcePatcherOnlyRewritesSafeLines()
        {
            string patched;

            True(PerfSourcePatcher.TryPatchTagCompare("if (x.tag == \"Player\")", out patched), "正向 tag 比较应被改写");
            Equal("if (x.CompareTag(\"Player\"))", patched, "正向改写结果");

            True(PerfSourcePatcher.TryPatchTagCompare("if (x.tag != \"Enemy\")", out patched), "反向 tag 比较应被改写");
            Equal("if (!x.CompareTag(\"Enemy\"))", patched, "反向改写要保留取反语义");

            True(PerfSourcePatcher.TryPatchTagCompare("if (\"Player\" == x.tag)", out patched), "字面量在左侧也应被改写");
            Equal("if (x.CompareTag(\"Player\"))", patched, "字面量在左侧的改写结果");

            // 拿不准的一律不动
            True(!PerfSourcePatcher.TryPatchTagCompare("bool same = a.tag == b.tag;", out patched), "两边都是 tag 变量时不该改写");
            True(!PerfSourcePatcher.TryPatchTagCompare("// x.tag == \"Player\"", out patched), "注释行不该改写");
            True(!PerfSourcePatcher.TryPatchTagCompare("if (x.CompareTag(\"Player\"))", out patched), "已经是 CompareTag 不应重复改写");

            // GC.Collect：只有它独占一行才敢注释
            True(PerfSourcePatcher.TryCommentGcCollect("        GC.Collect();", out patched), "独占一行的 GC.Collect 应被处理");
            True(patched.Contains("//"), "处理结果应当保留原行作为注释");
            True(!PerfSourcePatcher.TryCommentGcCollect("if (GC.Collect() != null) Do();", out patched), "混在表达式里会改坏语法，必须拒绝");
            True(!PerfSourcePatcher.TryCommentGcCollect("// GC.Collect();", out patched), "已注释的行不重复处理");
        }

        // =====================================================================
        // 长时间采集：降采样与时长模式进度
        // =====================================================================
        static void FrameDownsamplePreservesShapeAndBounds()
        {
            // 短采集不动：不做无谓的拷贝
            var shortList = Frames(100);
            True(ReferenceEquals(shortList, FrameStats.Downsample(shortList, 3000)),
                "不足上限时应原样返回，不做无谓拷贝");

            // 长采集均匀抽取到上限
            var longList = Frames(20000);
            var sampled = FrameStats.Downsample(longList, 3000);
            Equal(3000, sampled.Count, "应抽取到上限条数");

            // 首尾都要保留，否则会把流程开头与结尾的关键阶段切掉
            Equal(0, sampled[0].frame, "必须保留第一帧");
            True(sampled[sampled.Count - 1].frame >= longList[longList.Count - 1].frame - 10,
                "必须保留末尾帧");

            // 保持时间顺序：曲线不能乱
            for (int i = 1; i < sampled.Count; i++)
                True(sampled[i].frame > sampled[i - 1].frame, "抽样结果必须保持时间顺序");

            // 非法上限不应崩，也不应返回空
            Equal(20000, FrameStats.Downsample(longList, 0).Count, "max=0 时应原样返回");
            Equal(20000, FrameStats.Downsample(longList, -1).Count, "max 为负时应原样返回");
            True(FrameStats.Downsample(null, 100) == null, "null 应安全返回");
        }

        static void PlayModeTestJobProgressHandlesDurationMode()
        {
            var job = new PlayModeTestJob();
            job.warmupFrames = 60;
            job.captureFrames = 100000;
            job.durationSeconds = 120;
            job.phase = PlayModeTestPhase.Capturing;

            // 刚开采样：进度应落在采样段起点附近，而不是 0（那会让人以为没动）
            job.captureStartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            double p = job.Progress();
            True(p >= 0.1 && p < 0.3, "刚开采样时进度应在采样段起点附近，实际 " + p.ToString("0.###"));

            // 过了一半时长
            job.captureStartedUtc = DateTime.UtcNow.AddSeconds(-60).ToString("o", CultureInfo.InvariantCulture);
            p = job.Progress();
            True(p > 0.4 && p < 0.7, "过一半时长时进度应在中段，实际 " + p.ToString("0.###"));

            // 超过时长但还没收尾：不能报 100%，否则界面会显示已完成而实际还在跑
            job.captureStartedUtc = DateTime.UtcNow.AddSeconds(-500).ToString("o", CultureInfo.InvariantCulture);
            Equal(0.99, job.Progress(), "采样阶段进度封顶 0.99");

            job.phase = PlayModeTestPhase.Finalizing;
            Equal(1.0, job.Progress(), "收尾阶段算完成");
        }

        static void ToolResultFolderFoldsWithoutBreakingPairs()
        {
            var messages = new List<object>();

            var sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = "system prompt";
            messages.Add(sys);

            var user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = "为什么卡？";
            messages.Add(user);

            // 模拟 4 轮工具调用，每轮结果都很大
            for (int i = 0; i < 4; i++)
            {
                var assistant = new Dictionary<string, object>();
                assistant["role"] = "assistant";
                assistant["content"] = "";

                var call = new Dictionary<string, object>();
                call["id"] = "call_" + i.ToString(CultureInfo.InvariantCulture);
                var fn = new Dictionary<string, object>();
                fn["name"] = "perf_get_metrics";
                fn["arguments"] = "{}";
                call["function"] = fn;

                var calls = new List<object>();
                calls.Add(call);
                assistant["tool_calls"] = calls;
                messages.Add(assistant);

                var result = new Dictionary<string, object>();
                result["role"] = "tool";
                result["tool_call_id"] = "call_" + i.ToString(CultureInfo.InvariantCulture);
                result["content"] = new string('x', 5000);
                messages.Add(result);
            }

            int folded = ToolResultFolder.Fold(messages, 2, 200);
            Equal(2, folded, "4 条里应折叠较早的 2 条，保留最近 2 条");

            // 消息条数一条都不能少：删掉会让 OpenAI 协议校验直接失败
            Equal(10, messages.Count, "折叠只能改内容，不能删消息");

            string newest = ((Dictionary<string, object>)messages[9])["content"].ToString();
            string secondNewest = ((Dictionary<string, object>)messages[7])["content"].ToString();
            Equal(5000, newest.Length, "最近一条结果必须保持原文");
            Equal(5000, secondNewest.Length, "倒数第二条结果必须保持原文");

            string oldest = ((Dictionary<string, object>)messages[3])["content"].ToString();
            True(oldest.Length < 600, "折叠后应显著变短，实际 " + oldest.Length);
            True(oldest.Contains("perf_get_metrics"), "占位符里要带上工具名，模型才能重新调用");
            True(oldest.Contains("folded"), "占位符应有 folded 标记");
            True(oldest.Contains("5000"), "占位符应记录原始长度，便于判断是否值得重取");

            // 幂等：再折一次不应有变化（避免重复改写自己生成的占位符）
            Equal(0, ToolResultFolder.Fold(messages, 2, 200), "已折叠的占位符不应被反复处理");
        }

        // =====================================================================
        // 代码修复建议：模型输出格式不受控，解析必须钉死
        // =====================================================================
        static void CodeFixParserExtractsSuggestion()
        {
            string code, note;

            True(PerfCodeFixParser.TryExtract(
                "问题在于每次都在重新查找相机。\n\n```csharp\nprivate Camera _cam;\nvoid Awake() { _cam = Camera.main; }\n```\n\n这样只查一次。",
                out code, out note), "应能抽出代码块");
            True(code != null && code.Contains("_cam"), "代码块内容应被完整取出");
            True(!code.Contains("```"), "抽出的代码里不应残留围栏标记");
            True(note.Contains("每次都在重新查找"), "代码块之前的说明应保留");
            True(note.Contains("只查一次"), "代码块之后的说明应保留");

            // 模型拒绝给代码、只说明原因 —— 这也是有效结果，不能当失败丢掉
            True(!PerfCodeFixParser.TryExtract("这里需要引入对象池，无法局部安全修复。", out code, out note),
                "没有代码块时应返回 false");
            True(note.Contains("对象池"), "拒绝时也要把说明带出来，否则用户看不到原因");

            // 带语言标记
            True(PerfCodeFixParser.TryExtract("```cs\nvar x = 1;\n```", out code, out note), "```cs 也应识别");
            Equal("var x = 1;", code, "语言标记不应混进代码");

            // 空代码块 = 没给建议
            True(!PerfCodeFixParser.TryExtract("```csharp\n```", out code, out note), "空代码块应视为无建议");

            // 未闭合的围栏不应崩
            True(!PerfCodeFixParser.TryExtract("```csharp\nvar x = 1;", out code, out note), "未闭合围栏不应崩");

            // 空输入
            True(!PerfCodeFixParser.TryExtract("", out code, out note), "空输入不应崩");
            True(!PerfCodeFixParser.TryExtract(null, out code, out note), "null 不应崩");
        }

        // =====================================================================
        // 反模式标识的解析
        //
        // 这组测试是为了钉死一个曾经静默失效的 bug：codeIssues 里存的 pattern
        // 是 "方法名 + 反模式id" 的复合串，而修复映射拿它直接去比 "tag_compare"，
        // 永远不等 —— 表现为代码类结论全只有「人工」两个字。
        // =====================================================================
        static void CodePatternIsParsedBeforeMatching()
        {
            Equal("linq", CodeIssue.BasePattern("OnGUI + linq"), "应取出反模式 id");
            Equal("OnGUI", CodeIssue.MethodName("OnGUI + linq"), "应取出方法名");

            Equal("linq", CodeIssue.BasePattern("linq"), "没有前缀时原样返回");
            Equal("", CodeIssue.MethodName("linq"), "没有前缀时方法名为空");

            Equal("", CodeIssue.BasePattern(""), "空串不应崩");
            Equal("", CodeIssue.BasePattern(null), "null 不应崩");

            // 关键：修复计划必须能为这种复合 pattern 产出**可操作**的步骤
            var snapshot = CleanSnapshot();
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 10, "Update + tag_compare"));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "code_Update___tag_compare");
            True(plan != null, "应当产出修复计划");
            True(plan.HasExecutable, "tag_compare 出现在 Update 里也必须能机械改写");
            True(plan.SafeStepCount == 0, "源码改写不应被标为低风险（否则会被批量执行误伤）");

            // 非机械可改的模式至少要有 AI 改写入口，不能只剩「人工」
            var snapshot2 = CleanSnapshot();
            snapshot2.codeIssues.Add(CodeIssueOf("Assets/Scripts/B.cs", 20, "Update + linq"));
            Evaluate(snapshot2);

            var plan2 = PlanFor(snapshot2, "code_Update___linq");
            True(plan2 != null, "linq 应当产出修复计划");

            bool hasAiRewrite = false;
            for (int i = 0; i < plan2.steps.Count; i++)
                if (plan2.steps[i].kind == FixKind.AiRewrite && plan2.steps[i].targetCount > 0) hasAiRewrite = true;
            True(hasAiRewrite, "linq 这类问题必须给 AI 改写入口，而不是只有「人工」");
        }

        static List<FrameStat> Frames(int count)
        {
            var list = new List<FrameStat>(count);
            for (int i = 0; i < count; i++)
            {
                var f = new FrameStat();
                f.frame = i;
                f.deltaMs = 16.6;
                list.Add(f);
            }
            return list;
        }

        static SceneIssue SceneIssueOf(string code)
        {
            var issue = new SceneIssue();
            issue.code = code;
            issue.hierarchyPath = "Root/Child";
            issue.componentType = "Test";
            issue.issue = "测试问题";
            issue.detail = "";
            issue.suggestion = "测试建议";
            issue.severity = Severity.Warn;
            return issue;
        }

        static CodeIssue CodeIssueOf(string file, int line, string pattern)
        {
            var issue = new CodeIssue();
            issue.file = file;
            issue.line = line;
            issue.pattern = pattern;
            issue.snippet = "test";
            issue.suggestion = "测试建议";
            issue.severity = Severity.Warn;
            return issue;
        }

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

        static MetricDelta Metric(PerfDiffResult diff, string name)
        {
            for (int i = 0; i < diff.metrics.Count; i++)
                if (diff.metrics[i].name == name) return diff.metrics[i];
            throw new InvalidOperationException("diff result is missing metric: " + name);
        }

        static void Budgeted(PerfSnapshot snapshot, string metric, string budget)
        {
            var m = snapshot.FindMetric(metric);
            m.budget = budget;
            m.budgetUnit = m.unit;
        }

        static PerfFinding DiffFinding(string id, string severity)
        {
            return new PerfFinding { id = id, category = "内存", severity = severity, title = "结论 " + id };
        }

        static PerfSnapshot DiffSnapshot(string id, string capturedUtc)
        {
            return new PerfSnapshot { id = id, label = id, capturedUtc = capturedUtc };
        }

        static void AssertFinding(PerfSnapshot snapshot, string id, string category, string severity)
        {
            var finding = Single(snapshot, f => f.id == id);
            Equal(category, finding.category, id + " category");
            Equal(severity, finding.severity, id + " severity");
            Evidence(finding);
        }

        static PerfFinding Single(PerfSnapshot snapshot, Predicate<PerfFinding> predicate)
        {
            var matches = Evaluate(snapshot).FindAll(predicate);
            True(matches.Count == 1, "expected exactly one target finding, got " + matches.Count);
            return matches[0];
        }

        static List<PerfFinding> Evaluate(PerfSnapshot snapshot) { return new PerfRuleEngine().Evaluate(snapshot, Budget); }

        static void Evidence(PerfFinding finding)
        {
            True(finding.evidence != null && finding.evidence.Count > 0, "finding has no evidence");
            True(finding.evidence.TrueForAll(e => !string.IsNullOrEmpty(e.tool) && !string.IsNullOrEmpty(e.metric)), "evidence is not traceable");
        }

        // =====================================================================
        // 数据保真度
        // 验证点：与 Profiler 窗口同源的口径优先；拿不到时必须明确降级，不能用弱口径冒充
        // =====================================================================

        static void RecorderGcAllocIsPreferredWhenAvailable()
        {
            var snapshot = CleanSnapshot();
            for (int i = 0; i < 10; i++)
                snapshot.frames.Add(new FrameStat { frame = i, deltaMs = 8, managedAllocBytes = 1000, allocInFrameBytes = 4000 });

            True(snapshot.HasRecorderGcAlloc(), "recorder gc alloc should be detected");
            Equal(4000.0, snapshot.AvgRecorderAllocPerFrame(), "recorder average");
            Equal(1000.0, snapshot.AvgManagedAllocBytesPerFrame(), "diff-based average must stay available as cross-check");
        }

        static void MissingRecorderGcAllocDegradesExplicitly()
        {
            var snapshot = CleanSnapshot();
            for (int i = 0; i < 10; i++)
                snapshot.frames.Add(new FrameStat { frame = i, deltaMs = 8, managedAllocBytes = 1000 });

            True(!snapshot.HasRecorderGcAlloc(), "missing recorder counter must be reported as unavailable");
            True(double.IsNaN(snapshot.AvgRecorderAllocPerFrame()),
                "unavailable recorder must return NaN so callers degrade explicitly instead of using a wrong number");
        }

        static void Equal(double expected, double actual, string label)
        {
            True(Math.Abs(expected - actual) < 1e-6, label + ": expected " + expected + ", got " + actual);
        }

        static void Equal(string expected, string actual, string label) { True(expected == actual, label + ": expected " + expected + ", got " + actual); }
        static void Equal(int expected, int actual, string label) { True(expected == actual, label + ": expected " + expected + ", got " + actual); }
        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

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
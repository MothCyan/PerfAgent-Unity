using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Agent;
using PerfAgent.Analysis;
using PerfAgent.Collectors;
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
            var tests = new List<Action>
            {
                GcAllocExplosion,
                EditorOverheadIsNotReportedAsProjectProblem,
                EmptyProjectPlayModeAllocStaysUnattributed,
                TinyCaptureWindowSuppressesSampleDependentVerdicts,
                MissingBaselineSuppressesPerFrameAllocVerdict,
                ProjectAllocAboveNoiseBandStillFires,
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
                SceneFindingsHaveExecutableFixes,
                DrawCallFindingOffersAtlasFix,
                CodeFindingsExposeNoAutoFix,
                IsolatedStallIsNotReportedAsProjectJitter,
                RepeatedSpikesWithoutCorroborationStayUnattributed,
                SlowFramesWithAllocationSpikeAreAttributed,
                AbsurdFrameTimeIsRejectedInsteadOfReported,
                FrameDownsamplePreservesShapeAndBounds,
                ToolResultFolderFoldsWithoutBreakingPairs,
                CodePatternIsParsedBeforeMatching,
                ColumnLayoutDetectsUnity2022HierarchyColumns,
                ColumnLayoutExcludesTimestampColumns,
                ColumnLayoutParsesBareDecimalsAsTime,
                ColumnLayoutParsesByteUnits,
                WaveformKeepsNewestWhenFull,
                WaveformIgnoresInvalidSamples,
                WaveformPercentilesUseNearestSample,
                WaveformChartCeilingNeverTinyAndClears
            };

            // 本地规则引擎怎么回答提问（纯本地模式的那条路）单独一个文件，便于继续加用例
            LocalAnswerTests.Register(tests);

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

            Console.WriteLine("Result: " + (tests.Count - failed) + "/" + tests.Count + " passed");
            return failed == 0 ? 0 : 1;
        }

        static void GcAllocExplosion()
        {
            var snapshot = CleanSnapshot();
            SetPerFrameAlloc(snapshot, 64 * 1024, 8 * 1024);   // 项目自身约 56 KB/帧
            AssertFinding(snapshot, "gc_alloc_per_frame", "内存", Severity.Error);
        }

        /// <summary>
        /// 空工程回归：「每帧托管分配」很吓人，但绝大部分是编辑器自身的开销 —— 不能报成项目问题。
        /// 数字取自本机实测：空工程两次采集分别是 102636 B/帧（含编辑器开销）与 97409 B/帧 的编辑器基线。
        /// 不扣基线就会把一个空场景报成「严重：每帧分配超预算 50 倍」。
        /// </summary>
        static void EditorOverheadIsNotReportedAsProjectProblem()
        {
            var snapshot = CleanSnapshot();
            SetPerFrameAlloc(snapshot, 102636, 97409);

            True(snapshot.FindMetric("项目每帧分配").value > Budget.maxManagedAllocBytesPerFrame,
                "residual must still exceed the budget, otherwise this test proves nothing");
            True(Findings(snapshot, "gc_alloc_per_frame").Count == 0,
                "editor overhead must not be reported as a project-level gc alloc problem");
        }

        /// <summary>没有基线就无法归因：宁可不报，也不能把含编辑器开销的数字当成项目问题。</summary>
        static void MissingBaselineSuppressesPerFrameAllocVerdict()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("每帧托管分配", "B", 102636, "ProfilerRecorder: GC Allocated In Frame");

            True(Findings(snapshot, "gc_alloc_per_frame").Count == 0,
                "without an editor baseline the number is unattributable and must not be reported");
        }

        /// <summary>正向对照：项目自身分配明显越过基线噪声带时，仍然要报出来。</summary>
        static void ProjectAllocAboveNoiseBandStillFires()
        {
            var snapshot = CleanSnapshot();
            SetPerFrameAlloc(snapshot, 97409 + 60000, 97409);

            var finding = Single(snapshot, f => f.id == "gc_alloc_per_frame");
            Equal(Severity.Error, finding.severity, "gc_alloc_per_frame severity");
            True(finding.title.IndexOf("项目每帧托管分配", StringComparison.Ordinal) >= 0,
                "title must name the project-attributable number: " + finding.title);
            Evidence(finding);
        }

        /// <summary>
        /// 实测回归：空工程（SampleScene，4 个 Light、24 个 Draw Call、无用户脚本）在编辑器里跟随采集，
        /// 「每帧托管分配」= 14435 B/帧、编辑模式空闲基线 = 465 B/帧，残差 13970 B/帧。
        /// 这组数字曾经让工具报出「严重：项目每帧托管分配约 13970 B，超出预算 2048 B」，
        /// 但那些分配全都是编辑器自己 Play 模式下的开销（Game View 渲染、URP、Profiler 记录、Inspector 刷新）。
        ///
        /// 编辑模式基线（几百 B）与 Play 模式的实际开销（一万多 B）根本不是一个量级，
        /// 所以残差还必须越过「归因地板」才能归因到项目；过不了就只说「在编辑器里区分不出来」。
        /// </summary>
        static void EmptyProjectPlayModeAllocStaysUnattributed()
        {
            var snapshot = CleanSnapshot();
            snapshot.capturedFrameCount = 600;      // 窗口够长，把样本量闸门排除在外
            SetPerFrameAlloc(snapshot, 14435, 465);

            True(snapshot.FindMetric("项目每帧分配").value > Budget.maxManagedAllocBytesPerFrame,
                "residual must still exceed the player budget, otherwise this test proves nothing");
            True(Findings(snapshot, "gc_alloc_per_frame").Count == 0,
                "editor play-mode overhead must not be reported as a project-level gc alloc problem");
            True(snapshot.notes.Exists(n => n.IndexOf("归因闸门", StringComparison.Ordinal) >= 0),
                "the snapshot must say which gate the residual failed to pass");
        }

        /// <summary>
        /// 实测回归：跟随采集只录到 10 帧（约 0.7 秒）就退出 Play，
        /// 工具却给出「均值 2.28 / P50 2.27 / P95 2.3 / 峰值 2.3 ms」——
        /// 2 帧算出来的 P50/P95/峰值会完全相同，看着精确、实际毫无意义。
        /// 样本量不足时只说明原因，不下统计结论（这里故意把帧耗时放到远超预算的位置）。
        /// </summary>
        static void TinyCaptureWindowSuppressesSampleDependentVerdicts()
        {
            var snapshot = CleanSnapshot();
            snapshot.capturedFrameCount = 10;
            snapshot.SetMetric("帧耗时均值", "ms", 100.0, "Profiler 面板抽样 2/10 帧");
            for (int i = 0; i < 10; i++)
                snapshot.frames.Add(new FrameStat { frame = 100 + i, deltaMs = 100.0 });

            True(100.0 > Budget.FrameBudgetMs(), "frame time must be over budget, otherwise this test proves nothing");

            var findings = Evaluate(snapshot);
            Equal(0, findings.FindAll(f => f.id == "frame_time_over").Count,
                "a tiny window must not produce a frame-time error");
            Equal(0, findings.FindAll(f => f.id == "frame_time_jitter").Count,
                "a tiny window must not produce a jitter verdict");

            var small = Single(snapshot, f => f.id == "sample_too_small");
            Equal(Severity.Info, small.severity, "sample_too_small severity");
            Evidence(small);
        }

        /// <summary>
        /// 空工程回归：编辑器自身的孤立停顿不能被报成项目的帧耗时抖动。
        /// 实测数据：1650 帧里只有 19 帧超过 P95×1.5，峰值 87.84 ms 出现在「进入 Play 的第 2 帧」，
        /// 其余尖峰帧的分配（~16.9 KB）与 Draw Call（24）跟普通帧完全一样。
        /// </summary>
        static void IsolatedStallIsNotReportedAsProjectJitter()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("帧耗时均值", "ms", 3.1, "FrameCapture/test");
            for (int i = 0; i < 200; i++)
            {
                bool slow = i == 5 || i == 40 || i == 120;
                snapshot.frames.Add(new FrameStat
                {
                    frame = i,
                    deltaMs = slow ? 40.0 : 3.0,
                    allocInFrameBytes = 16871,
                    drawCalls = 24
                });
            }

            True(snapshot.FrameTimeMaxMs() > snapshot.FrameTimePercentileMs(95) * 1.5,
                "this test only proves something if a spike exists");
            True(Findings(snapshot, "frame_time_jitter").Count == 0,
                "an isolated editor stall must not be reported as a project jitter problem");
            True(Findings(snapshot, "spike_attribution").Count == 0,
                "without evidence on the game side there is nothing to attribute");
            True(snapshot.notes.Exists(n => n.IndexOf("无法归因到项目", StringComparison.Ordinal) >= 0),
                "the snapshot must explain why the spikes were not attributed");
        }

        /// <summary>尖峰反复出现（占比越过门槛）时仍然报抖动，但没有游戏侧佐证时不给归因。</summary>
        static void RepeatedSpikesWithoutCorroborationStayUnattributed()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("帧耗时均值", "ms", 3.1, "FrameCapture/test");
            for (int i = 0; i < 1000; i++)
            {
                bool slow = i >= 970;
                snapshot.frames.Add(new FrameStat
                {
                    frame = i,
                    deltaMs = slow ? 40.0 : 3.0,
                    allocInFrameBytes = 16871,
                    drawCalls = 24
                });
            }

            True(Findings(snapshot, "frame_time_jitter").Count == 1,
                "repeated spikes above the share threshold must still be reported");
            True(Findings(snapshot, "spike_attribution").Count == 0,
                "attribution must not be emitted when the game side shows nothing");
        }

        /// <summary>正向对照：尖峰帧带着明显更高的分配时，归因要照常给出。</summary>
        static void SlowFramesWithAllocationSpikeAreAttributed()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("帧耗时均值", "ms", 3.5, "FrameCapture/test");
            for (int i = 0; i < 1000; i++)
            {
                bool slow = i >= 985;
                snapshot.frames.Add(new FrameStat
                {
                    frame = i,
                    deltaMs = slow ? 40.0 : 3.0,
                    allocInFrameBytes = slow ? 200000 : 16000,
                    drawCalls = 24
                });
            }

            Equal(1, Findings(snapshot, "frame_time_jitter").Count, "corroborated jitter count");
            var attribution = Single(snapshot, f => f.id == "spike_attribution");
            Evidence(attribution);
            True(attribution.recommendation.IndexOf("GC", StringComparison.Ordinal) >= 0,
                "allocation-driven spikes should be described as GC-driven: " + attribution.recommendation);
        }

        /// <summary>
        /// 钉死 Unity 2022.3 的 Hierarchy 视图列布局。
        /// 行数据取自本机 API 探针的真实输出：
        ///   [0]=EditorLoop [1]=0.0% [2]=0.0% [3]=1 [4]=0 B [5]=0.06 [6]=0.06 [7]=N/A
        /// 旧实现只认带 "ms" 后缀的字符串，于是 [5][6] 全解析成 0，
        /// 排行有 60 行却一条可用数据都没有 —— 这条用例就是专门固定这个 bug。
        /// </summary>
        static void ColumnLayoutDetectsUnity2022HierarchyColumns()
        {
            var rows = new List<string[]>
            {
                new[] { "EditorLoop", "0.0%", "0.0%", "1", "0 B", "0.06", "0.06", "N/A" },
                new[] { "Camera.Render", "65.3%", "12.4%", "1", "1.2 KB", "3.42", "0.65", "N/A" },
                new[] { "PlayerLoop", "70.1%", "0.2%", "1", "0 B", "3.67", "0.01", "N/A" },
                new[] { "GC.Alloc", "3.1%", "3.1%", "17", "8.31 MB", "0.16", "0.16", "N/A" }
            };

            var layout = ProfilerColumnLayout.Detect(rows);
            Equal(8, layout.columnCount, "column count");
            True(layout.IsUsable, "layout must be usable");
            Equal(3, layout.callsColumn, "calls column");
            Equal(4, layout.gcAllocColumn, "gc alloc column");
            Equal(2, layout.msColumns.Count, "time column count");
            Equal(5, layout.msColumns[0], "first time column");
            Equal(6, layout.msColumns[1], "second time column");
            Equal(2, layout.percentColumns.Count, "percent column count");
            Equal(1, layout.percentColumns[0], "first percent column");

            // 名称列（[0]）不能被认成任何数值列
            True(!layout.msColumns.Contains(0) && layout.callsColumn != 0 && layout.gcAllocColumn != 0,
                "the name column must not be classified as numeric");
        }

        /// <summary>
        /// 实测的 15 列布局（Unity 2022.3，Play 模式，探针输出）：
        ///   [0]名称 [1][2]百分比 [3]调用次数 [4]GC Alloc [5][6]耗时 [7..13]N/A 或空 [14]帧起始时间戳
        /// [14] 是裸小数（3627373363.20），只靠「裸小数 = 耗时」会把它也当成耗时列，
        /// 再用「耗时列取最大值当总耗时」就得到 36 亿 ms/帧 —— 这条用例钉死「大数一律排除」。
        /// </summary>
        static void ColumnLayoutExcludesTimestampColumns()
        {
            var rows = new List<string[]>
            {
                new[] { "EditorLoop", "0.0%", "0.0%", "1", "0 B", "0.06", "0.06", "N/A", "N/A", "N/A", "N/A", "N/A", "", "N/A", "3627373363.20" },
                new[] { "Camera.Render", "65.3%", "12.4%", "1", "1.2 KB", "3.42", "0.65", "N/A", "N/A", "N/A", "N/A", "N/A", "", "N/A", "3627373371.55" },
                new[] { "PlayerLoop", "70.1%", "0.2%", "1", "0 B", "3.67", "0.01", "N/A", "N/A", "N/A", "N/A", "N/A", "", "N/A", "3627373375.12" }
            };

            var layout = ProfilerColumnLayout.Detect(rows);
            Equal(15, layout.columnCount, "column count");
            Equal(2, layout.msColumns.Count, "time column count");
            Equal(5, layout.msColumns[0], "first time column");
            Equal(6, layout.msColumns[1], "second time column");
            Equal(1, layout.timestampColumns.Count, "timestamp column count");
            Equal(14, layout.timestampColumns[0], "timestamp column index");
            True(!layout.msColumns.Contains(14), "the timestamp column must not be treated as a duration");
            Equal(4, layout.gcAllocColumn, "gc alloc column");
            Equal(3, layout.callsColumn, "calls column");
        }

        /// <summary>裸小数（无单位）必须被当成耗时 —— 这正是旧实现漏掉的情况。</summary>
        static void ColumnLayoutParsesBareDecimalsAsTime()
        {
            Equal(0.06, ProfilerColumnLayout.ParseNumber("0.06"), "bare decimal");
            Equal(1234.5, ProfilerColumnLayout.ParseNumber("1,234.5"), "thousands separator");
            Equal(12.3, ProfilerColumnLayout.ParseNumber("12.3ms"), "with ms suffix");
            Equal(0.0, ProfilerColumnLayout.ParseNumber("0.0%"), "percent");
            True(double.IsNaN(ProfilerColumnLayout.ParseNumber("N/A")), "N/A must not become a number");
            True(double.IsNaN(ProfilerColumnLayout.ParseNumber("")), "empty must not become a number");
        }

        /// <summary>字节列必须按单位换算，而不是当成裸数字。</summary>
        static void ColumnLayoutParsesByteUnits()
        {
            Equal(0L, ProfilerColumnLayout.ParseBytes("0 B"), "zero bytes");
            Equal(1229L, ProfilerColumnLayout.ParseBytes("1.2 KB"), "kilobytes");
            Equal(8713667L, ProfilerColumnLayout.ParseBytes("8.31 MB"), "megabytes");
            Equal(4L, ProfilerColumnLayout.ParseCount("4"), "calls");
            Equal(0L, ProfilerColumnLayout.ParseCount("N/A"), "unknown calls");
        }

        /// <summary>
        /// 荒谬的帧耗时读数不能变成「严重：帧耗时超预算」。
        /// 实测：面板列语义错位，把一列字节数当成耗时列，算出 3627373035 ms/帧，
        /// 然后被报成严重结论 —— 这条用例钉死这个行为。
        /// </summary>
        static void AbsurdFrameTimeIsRejectedInsteadOfReported()
        {
            var snapshot = CleanSnapshot();
            snapshot.SetMetric("帧耗时均值", "ms", 3627372582.23, "Profiler 面板抽样 60/300 帧");
            for (int i = 0; i < 60; i++)
                snapshot.frames.Add(new FrameStat { frame = 1965 + i * 5, deltaMs = 3627372052.48 + i });

            True(Findings(snapshot, "frame_time_over").Count == 0,
                "absurd frame time must not become an error finding");
            True(Findings(snapshot, "frame_time_jitter").Count == 0,
                "absurd frame time must not become a jitter finding");
            True(Findings(snapshot, "spike_attribution").Count == 0,
                "absurd frame time must not produce spike attribution");
            True(snapshot.notes.Exists(n => n.IndexOf("不合理", StringComparison.Ordinal) >= 0),
                "the snapshot must explain why frame time was skipped");
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
            SetPerFrameAlloc(snapshot, 64 * 1024, 8 * 1024);
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

        // 代码类结论只给「跳转 + 人工」：本插件不提供改写脚本源码的能力。
        // 这条断言是故意写死的 —— 重新把自动改代码加回来之前，先想清楚风险。
        static void CodeFindingsExposeNoAutoFix()
        {
            var snapshot = CleanSnapshot();
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 10, "tag_compare"));
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 20, "gc_collect"));
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/B.cs", 5, "camera_main"));
            Evaluate(snapshot);

            string[] ids = { "code_tag_compare", "code_gc_collect", "code_camera_main" };
            for (int i = 0; i < ids.Length; i++)
            {
                var plan = PlanFor(snapshot, ids[i]);
                True(plan != null, ids[i] + " 应当产出计划");
                True(!plan.HasExecutable, ids[i] + " 不能给执行按钮：插件不自动改写源码");
                Equal(0, plan.ExecutableStepCount, ids[i] + " 不应有任何可执行步骤");
                True(HasNavigateStep(plan), ids[i] + " 应当提供跳转到源码的步骤");
                True(HasManualStep(plan), ids[i] + " 应当给出人工处理步骤");
            }
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

            // 关键：复合 pattern 也必须能被计划识别出来（曾经因为没解析前缀而整条链路落空）
            var snapshot = CleanSnapshot();
            snapshot.codeIssues.Add(CodeIssueOf("Assets/Scripts/A.cs", 10, "Update + tag_compare"));
            Evaluate(snapshot);

            var plan = PlanFor(snapshot, "code_Update___tag_compare");
            True(plan != null, "应当产出修复计划");
            True(HasNavigateStep(plan), "复合 pattern 也要能定位到具体行");
            True(!plan.HasExecutable, "代码类结论不应有执行按钮");

            var snapshot2 = CleanSnapshot();
            snapshot2.codeIssues.Add(CodeIssueOf("Assets/Scripts/B.cs", 20, "Update + linq"));
            Evaluate(snapshot2);

            var plan2 = PlanFor(snapshot2, "code_Update___linq");
            True(plan2 != null, "linq 应当产出修复计划");
            True(HasNavigateStep(plan2), "linq 也要能定位到具体行");
            True(!plan2.HasExecutable, "代码类结论不应有执行按钮");
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

        static List<PerfFinding> Findings(PerfSnapshot snapshot, string id)
        {
            return Evaluate(snapshot).FindAll(f => f.id == id);
        }

        /// <summary>
        /// 按「实测 − 编辑器基线」的口径写指标。
        /// 「每帧托管分配」是含编辑器开销的实测值，规则只看扣掉基线后的「项目每帧分配」。
        /// </summary>
        static void SetPerFrameAlloc(PerfSnapshot s, double raw, double editorBaseline)
        {
            s.SetMetric("每帧托管分配", "B", raw, "ProfilerRecorder: GC Allocated In Frame");
            s.SetMetric("编辑器开销基线", "B", editorBaseline, "编辑模式空转实测");
            s.SetMetric("项目每帧分配", "B", Math.Max(0, raw - editorBaseline), "估算：实测 − 基线");
        }

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

        // =====================================================================
        // 实时帧率波形（面板上那条曲线用的就是它）
        //
        // 这批用例盯的是两件事：环缓冲写满后只能丢最旧的；
        // 以及「无效读数」绝不能进统计 —— 那会凭空把分位数拉低、造出好数据。
        // =====================================================================
        static void WaveformKeepsNewestWhenFull()
        {
            var w = new FrameWaveform(4);
            for (int i = 1; i <= 6; i++) True(w.Push(i), "push " + i);

            Equal(4, w.Count, "样本数必须被容量卡住");
            Equal(3.0, w[0], "最旧的三个应被丢掉");
            Equal(6.0, w[3], "最新样本必须落在末尾");
            Equal(6.0, w.MaxMs(), "峰值");
            True(w.IsFull, "写满后要能报出来");
        }

        static void WaveformIgnoresInvalidSamples()
        {
            var w = new FrameWaveform(8);

            True(!w.Push(0), "0 是「没读到」，不是「超快的一帧」");
            True(!w.Push(-1), "负数必须拒绝");
            True(!w.Push(double.NaN), "NaN 必须拒绝");
            True(!w.Push(double.PositiveInfinity), "无穷大必须拒绝");
            Equal(0, w.Count, "上面都不该产生样本");
            Equal(0.0, w.Fps(), "没有样本时不编帧率");

            True(w.Push(10), "有效样本");
            Equal(100.0, w.Fps(), "帧率由 P50 换算");
        }

        static void WaveformPercentilesUseNearestSample()
        {
            var w = new FrameWaveform(100);
            for (int i = 1; i <= 100; i++) w.Push(i);

            Equal(1.0, w.MinMs(), "最小");
            // 1..100 的「最近样本」中位是第 51 个（偶数个样本取上中位）——
            // 这个口径必须与快照里的统计一致，否则界面曲线与事后报告会对不上
            Equal(51.0, w.P50Ms(), "P50");
            Equal(95.0, w.P95Ms(), "P95");
            Equal(100.0, w.MaxMs(), "峰值");
            True(w.Fps() > 19.5 && w.Fps() < 19.7, "帧率 = 1000 / P50");
        }

        static void WaveformChartCeilingNeverTinyAndClears()
        {
            var w = new FrameWaveform(4);

            w.Push(8.0);
            Equal(33.0, w.ChartCeilingMs(), "平稳的 120 FPS 不能画成满格噪点");

            w.Push(90.0);
            Equal(90.0, w.ChartCeilingMs(), "真出现尖峰时纵轴要跟着抬高");

            w.Clear();
            Equal(0, w.Count, "清空");
            Equal(33.0, w.ChartCeilingMs(), "空曲线仍给一个不零平的纵轴");
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
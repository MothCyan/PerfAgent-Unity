using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.McpForUnity
{
    /// <summary>
    /// 把 PerfAgent 暴露给外部 MCP 客户端（Claude Desktop / VS Code Copilot 等）的桥。
    ///
    /// 边界（刻意为之）：
    ///  1. **只读 + 触发分析**。这里没有、也不会有「执行一键修复」的工具 ——
    ///     让外部模型直接改用户的工程资源风险太大，修改必须由人在 Unity 面板里点击确认。
    ///  2. 抓帧是唯一有副作用的操作（会占用编辑器几秒），因此拆成 start + status 两步，
    ///     避免请求长时间挂住，也让调用方能自己控制轮询节奏。
    ///  3. 返回的数据与面板/Agent 完全同源（同一份 PerfPipeline + PerfSnapshotStore），
    ///     不会出现「MCP 看到一套数、面板看到另一套」。
    ///
    /// 启用方式：菜单 Tools > PerfAgent > MCP 集成 > 启用（默认关闭，见该菜单的说明）。
    /// </summary>
    internal static class McpToolkit
    {
        public static string Str(JObject p, string key, string def = null)
        {
            if (p == null) return def;
            var token = p[key];
            if (token == null || token.Type == JTokenType.Null) return def;
            string value = token.ToString();
            return string.IsNullOrEmpty(value) ? def : value;
        }

        public static int Int(JObject p, string key, int def)
        {
            string raw = Str(p, key);
            int value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : def;
        }

        public static bool Bool(JObject p, string key, bool def)
        {
            string raw = Str(p, key);
            if (string.IsNullOrEmpty(raw)) return def;
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
        }

        /// <summary>按 snapshot_id 取快照；不传则用当前快照。</summary>
        public static PerfSnapshot ResolveSnapshot(JObject p, out string error)
        {
            error = null;
            string id = Str(p, "snapshot_id");
            if (string.IsNullOrEmpty(id)) id = Str(p, "snapshotId");

            if (string.IsNullOrEmpty(id))
            {
                if (PerfSession.Current == null)
                {
                    error = "当前没有快照。先调用 perf_static_audit（秒级）或 perf_capture_start 采集，"
                          + "或用 perf_list_snapshots 选一份历史快照。";
                    return null;
                }
                return PerfSession.Current;
            }

            var paths = PerfSnapshotStore.List();
            for (int i = 0; i < paths.Count; i++)
            {
                if (Path.GetFileNameWithoutExtension(paths[i]) != id) continue;
                var snap = PerfSnapshotStore.Load(paths[i]);
                if (snap == null)
                {
                    error = "快照文件存在但读取失败：" + id;
                    return null;
                }
                PerfPipeline.Analyze(snap);
                PerfSession.SetCurrent(snap, paths[i]);
                return snap;
            }

            error = "未找到快照：" + id + "。用 perf_list_snapshots 查看可用 id。";
            return null;
        }

        /// <summary>按 id 从磁盘加载快照，并把它设为当前快照（与面板同源）。</summary>
        public static PerfSnapshot LoadSnapshotById(string id, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(id)) return null;

            var paths = PerfSnapshotStore.List();
            for (int i = 0; i < paths.Count; i++)
            {
                if (Path.GetFileNameWithoutExtension(paths[i]) != id) continue;
                var snap = PerfSnapshotStore.Load(paths[i]);
                if (snap == null) return null;

                path = paths[i];
                PerfPipeline.Analyze(snap);
                PerfSession.SetCurrent(snap, path);
                return snap;
            }

            return null;
        }

        /// <summary>任务的事件流水，转成可序列化的列表。</summary>
        public static List<object> EventList(PlayModeTestJob job)
        {
            var list = new List<object>();
            if (job == null) return list;

            var events = job.Events;
            for (int i = 0; i < events.Count; i++) list.Add(events[i]);
            return list;
        }

        /// <summary>面板里设的默认采集时长（秒）；0 表示默认按帧数。</summary>
        public static int DefaultCaptureSeconds()
        {
            double seconds = PerfAgentSettings.Config.budget.captureSeconds;
            return seconds > 0 ? (int)Math.Min(seconds, PerfPipeline.MaxCaptureSeconds) : 0;
        }

        public static object Brief(PerfSnapshot s)
        {
            return new
            {
                snapshot_id = s.id,
                label = s.Label(),
                captured_utc = s.capturedUtc,
                scene = s.scenePath,
                frames = s.capturedFrameCount > 0 ? s.capturedFrameCount : s.frames.Count,
                stored_frames = s.frames.Count,
                metrics = s.metrics.Count,
                findings = s.findings.Count,
                asset_issues = s.assetIssues.Count,
                scene_issues = s.sceneIssues.Count,
                code_issues = s.codeIssues.Count,
                notes = s.notes,
            };
        }

        public static object Evidence(PerfFinding f)
        {
            var list = new List<object>(f.evidence.Count);
            for (int i = 0; i < f.evidence.Count; i++)
            {
                var e = f.evidence[i];
                list.Add(new
                {
                    tool = e.tool,
                    metric = e.metric,
                    value = e.value,
                    unit = e.unit,
                    threshold = e.threshold,
                    source = e.source,
                });
            }
            return list;
        }

        public static object Finding(PerfFinding f, bool withEvidence)
        {
            if (!withEvidence)
                return new { id = f.id, category = f.category, severity = f.severity, title = f.title, confidence = f.confidence };

            return new
            {
                id = f.id,
                category = f.category,
                severity = f.severity,
                title = f.title,
                detail = f.detail,
                recommendation = f.recommendation,
                confidence = f.confidence,
                jump_to = f.jumpTo,
                fix_code = f.fixCode,
                evidence = Evidence(f),
            };
        }
    }

    /// <summary>抓帧作业状态。域重载会重置，此时也会同步丢失 PerfSession.Capturing。</summary>
    internal static class PerfCaptureJob
    {
        public static bool Running;
        public static float Progress;
        public static int TargetFrames;
        /// <summary>>0 表示按时长采集，此时 TargetFrames 只是安全上限。</summary>
        public static double DurationSeconds;
        public static string StartedUtc = "";
        public static string FinishedUtc = "";
        public static string SnapshotId = "";
        public static string LastError = "";

        public static void Begin(int frames, double seconds)
        {
            Running = true;
            Progress = 0f;
            TargetFrames = frames;
            DurationSeconds = seconds;
            StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            FinishedUtc = "";
            SnapshotId = "";
            LastError = "";
        }

        public static void Complete(PerfSnapshot snapshot)
        {
            Running = false;
            Progress = 1f;
            FinishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            SnapshotId = snapshot == null ? "" : snapshot.id;
        }
    }

    // =========================================================================
    // 只读工具
    // =========================================================================

    [McpForUnityTool("perf_list_snapshots",
        Description = "列出本机已有的 Unity 性能快照（id / 标签 / 采集时间 / 帧数 / 结论数）。" +
                      "筛选条件为「你有几份快照、该看哪一份」时先用它。",
        Group = "core")]
    public static class PerfListSnapshots
    {
        public static object HandleCommand(JObject @params)
        {
            var paths = PerfSnapshotStore.List();
            int top = Math.Max(1, Math.Min(50, McpToolkit.Int(@params, "top", 20)));

            var list = new List<object>();
            for (int i = 0; i < paths.Count && i < top; i++)
            {
                var snap = PerfSnapshotStore.Load(paths[i]);
                if (snap == null) continue;
                list.Add(new
                {
                    snapshot_id = snap.id,
                    label = snap.Label(),
                    captured_utc = snap.capturedUtc,
                    scene = snap.scenePath,
                    frames = snap.frames.Count,
                    findings = snap.findings.Count,
                    metrics = snap.metrics.Count,
                });
            }

            return new SuccessResponse(
                paths.Count == 0 ? "本机还没有性能快照。" : ("共 " + paths.Count + " 份快照，返回最近 " + list.Count + " 份。"),
                new { count = paths.Count, snapshots = list });
        }
    }

    [McpForUnityTool("perf_get_findings",
        Description = "获取性能快照的诊断结论（规则引擎产出的确定性结论，非模型推断）。" +
                      "每条结论都带完整证据链（含产出该证据的 Unity API 名），可直接引用。",
        Group = "core")]
    public static class PerfGetFindings
    {
        public static object HandleCommand(JObject @params)
        {
            string error;
            var snap = McpToolkit.ResolveSnapshot(@params, out error);
            if (snap == null) return new ErrorResponse(error);

            string severity = McpToolkit.Str(@params, "severity");
            if (string.IsNullOrEmpty(severity)) severity = McpToolkit.Str(@params, "min_severity");
            int top = Math.Max(1, Math.Min(200, McpToolkit.Int(@params, "top", 50)));
            bool withEvidence = McpToolkit.Bool(@params, "include_evidence", true);

            var list = new List<object>();
            for (int i = 0; i < snap.findings.Count && list.Count < top; i++)
            {
                var f = snap.findings[i];
                if (!string.IsNullOrEmpty(severity) && Severity.Rank(f.severity) < Severity.Rank(severity)) continue;
                list.Add(McpToolkit.Finding(f, withEvidence));
            }

            return new SuccessResponse(
                "快照 " + snap.id + " 共 " + snap.findings.Count + " 条结论，返回 " + list.Count + " 条。",
                new { snapshot = McpToolkit.Brief(snap), findings = list });
        }
    }

    [McpForUnityTool("perf_get_metrics",
        Description = "获取快照的标量指标（值 / 预算 / 严重度 / 数据来源 API）。" +
                      "source 字段写明该数字来自哪个 Unity API，便于交叉验证。",
        Group = "core")]
    public static class PerfGetMetrics
    {
        public static object HandleCommand(JObject @params)
        {
            string error;
            var snap = McpToolkit.ResolveSnapshot(@params, out error);
            if (snap == null) return new ErrorResponse(error);

            string filter = McpToolkit.Str(@params, "filter");
            var list = new List<object>();

            for (int i = 0; i < snap.metrics.Count; i++)
            {
                var m = snap.metrics[i];
                if (!string.IsNullOrEmpty(filter) &&
                    m.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                list.Add(new
                {
                    name = m.name,
                    value = m.value,
                    unit = m.unit,
                    budget = string.IsNullOrEmpty(m.budget) ? null : m.budget,
                    budget_unit = m.budgetUnit,
                    severity = m.severity,
                    samples = m.samples,
                    source = m.source,
                });
            }

            return new SuccessResponse("快照 " + snap.id + " 的指标（" + list.Count + " 条）。",
                new { snapshot = McpToolkit.Brief(snap), metrics = list });
        }
    }

    [McpForUnityTool("perf_get_fix_plan",
        Description = "获取一键修复计划：每条结论对应可执行的修复动作（action_id / 风险等级 / 影响目标数 / 是否可撤销）" +
                      "以及必须人工处理的步骤。注意：本工具只返回计划，**执行入口只在 Unity 面板里**，外部无法触发修改。",
        Group = "core")]
    public static class PerfGetFixPlan
    {
        public static object HandleCommand(JObject @params)
        {
            string error;
            var snap = McpToolkit.ResolveSnapshot(@params, out error);
            if (snap == null) return new ErrorResponse(error);

            string findingId = McpToolkit.Str(@params, "finding_id");
            bool executableOnly = McpToolkit.Bool(@params, "executable_only", false);

            var plans = PerfFixPlanner.BuildPlans(snap);
            var list = new List<object>();

            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                if (!string.IsNullOrEmpty(findingId) && plan.findingId != findingId) continue;
                if (executableOnly && !plan.HasExecutable) continue;

                var steps = new List<object>();
                for (int k = 0; k < plan.steps.Count; k++)
                {
                    var s = plan.steps[k];
                    steps.Add(new
                    {
                        action_id = s.actionId,
                        kind = s.kind,
                        risk = s.risk,
                        executable = s.CanExecute,
                        title = s.title,
                        what_it_changes = s.detail,
                        expected_gain = s.expectedGain,
                        target_count = s.targetCount,
                        reversible = s.reversible,
                        targets_sample = s.targets,
                    });
                }

                list.Add(new
                {
                    finding_id = plan.findingId,
                    category = plan.category,
                    severity = plan.severity,
                    title = plan.title,
                    executable_step_count = plan.ExecutableStepCount,
                    low_risk_step_count = plan.SafeStepCount,
                    steps = steps,
                });
            }

            return new SuccessResponse(
                "快照 " + snap.id + " 共 " + list.Count + " 条计划。" +
                "执行必须由用户在 Unity 面板「结论」标签里点击确认，外部无法触发。",
                new { snapshot_id = snap.id, plans = list });
        }
    }

    // =========================================================================
    // 触发采集
    // =========================================================================

    [McpForUnityTool("perf_static_audit",
        Description = "立即跑一次静态审计（资源导入问题 / 场景与物理反模式 / 代码反模式 / 内存与渲染统计），" +
                      "不抓帧、秒级完成、没有副作用，是流水线里最快的入口。",
        Group = "core")]
    public static class PerfStaticAudit
    {
        public static object HandleCommand(JObject @params)
        {
            bool deep = McpToolkit.Bool(@params, "deep", false);
            try
            {
                var snap = PerfPipeline.StaticAudit(deep, null);
                return new SuccessResponse("静态审计完成。", McpToolkit.Brief(snap));
            }
            catch (Exception e)
            {
                return new ErrorResponse("静态审计失败：" + e.Message);
            }
        }
    }

    [McpForUnityTool("perf_capture_start",
        Description = "开始抓帧采集（会真实占用编辑器若干秒，帧数越多越久）。" +
                      "本工具立即返回，抓帧在编辑器主循环里进行；用 perf_capture_status 查进度与结果。",
        Group = "core")]
    public static class PerfCaptureStart
    {
        public static object HandleCommand(JObject @params)
        {
            if (PerfSession.Capturing || PerfCaptureJob.Running)
                return new ErrorResponse("已有抓帧任务在进行中。用 perf_capture_status 查看进度。");

            int frames = McpToolkit.Int(@params, "frames", 0);
            if (frames <= 0) frames = PerfAgentSettings.Config.budget.captureFrames;
            frames = Math.Max(10, Math.Min(PerfPipeline.MaxCaptureFrames, frames));

            // duration_seconds > 0 时按**时间**采集，frames 退化为安全上限。
            // 「跑完一整局游戏」是按秒描述的，只给帧数没法用。
            int seconds = McpToolkit.Int(@params, "duration_seconds", 0);
            if (seconds <= 0) seconds = McpToolkit.DefaultCaptureSeconds();
            double duration = seconds > 0 ? Math.Min((double)seconds, PerfPipeline.MaxCaptureSeconds) : 0;

            PerfCaptureJob.Begin(frames, duration);

            try
            {
                if (duration > 0)
                {
                    PerfPipeline.CaptureFrames(frames, duration,
                        delegate (PerfSnapshot snap) { PerfCaptureJob.Complete(snap); },
                        delegate (float p) { PerfCaptureJob.Progress = p; },
                        string.Format(CultureInfo.InvariantCulture, "MCP 抓帧 {0:0.#}s", duration));
                }
                else
                {
                    PerfPipeline.CaptureFrames(frames,
                        delegate (PerfSnapshot snap) { PerfCaptureJob.Complete(snap); },
                        delegate (float p) { PerfCaptureJob.Progress = p; },
                        "MCP 抓帧");
                }
            }
            catch (Exception e)
            {
                PerfCaptureJob.Running = false;
                PerfCaptureJob.LastError = e.Message;
                return new ErrorResponse("启动抓帧失败：" + e.Message);
            }

            return new SuccessResponse(
                duration > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "已开始按时长采集：{0:0.#} 秒（安全上限 {1} 帧）。实际帧数由帧率决定。请用 perf_capture_status 查询。",
                        duration, frames)
                    : ("已开始抓帧 " + frames + " 帧，预计需要约 " + frames + " 帧的真实时间。请稍后用 perf_capture_status 查询。"),
                new
                {
                    running = true,
                    frames = frames,
                    duration_seconds = duration > 0 ? duration : 0,
                    hint = duration > 0
                        ? "时长模式：跑完一整局/一整个流程用这个；用 frames 描述既不准也换算麻烦。"
                        : "帧数模式：想要固定采样量时用这个。",
                });
        }

    }

    [McpForUnityTool("perf_capture_status",
        Description = "查询抓帧进度。running=false 且 snapshot_id 非空表示已完成，可直接用该 snapshot_id 取结论与修复计划。",
        Group = "core")]
    public static class PerfCaptureStatus
    {
        public static object HandleCommand(JObject @params)
        {
            bool running = PerfCaptureJob.Running && PerfSession.Capturing;

            if (running)
            {
                double elapsed = 0;
                DateTime started;
                if (DateTime.TryParse(PerfCaptureJob.StartedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out started))
                    elapsed = (DateTime.UtcNow - started).TotalSeconds;

                return new SuccessResponse("抓帧中。", new
                {
                    running = true,
                    target_frames = PerfCaptureJob.TargetFrames,
                    progress = Math.Round(PerfCaptureJob.Progress, 3),
                    elapsed_seconds = Math.Round(elapsed, 1),
                });
            }

            if (!string.IsNullOrEmpty(PerfCaptureJob.SnapshotId))
            {
                var paths = PerfSnapshotStore.List();
                for (int i = 0; i < paths.Count; i++)
                {
                    if (Path.GetFileNameWithoutExtension(paths[i]) != PerfCaptureJob.SnapshotId) continue;
                    var snap = PerfSnapshotStore.Load(paths[i]);
                    if (snap != null)
                        return new SuccessResponse("抓帧已完成。",
                            new { running = false, snapshot = McpToolkit.Brief(snap) });
                }
            }

            return new SuccessResponse("当前没有抓帧任务。", new
            {
                running = false,
                last_error = string.IsNullOrEmpty(PerfCaptureJob.LastError) ? null : PerfCaptureJob.LastError,
                hint = "用 perf_static_audit（秒级）或 perf_capture_start 开始一次分析。",
            });
        }
    }

    // =========================================================================
    // 自动走游戏循环并采集性能
    //
    // 这几个工具是桥接层里唯一会让编辑器发生「状态迁移」的入口（进 / 出 Play 模式），
    // 所以刻意拆成 start + status + result + cancel 四段：
    //   1. 流程要穿过两次域重载，MCP 连接会被切断，单次调用不可能阻塞等待；
    //   2. 状态全部落在 ProjectSettings/PerfAgent/PlayModeRuns，外部可以反复查询；
    //   3. 出问题时能单独取消，不让编辑器卡在 Play 模式里出不来。
    // 与抓帧工具一样：这里只负责「测量」，不含任何修改工程资源的动作。
    // =========================================================================

    [McpForUnityTool("perf_playmode_test_start",
        Description = "启动**自动** Play 模式性能测试：进入 Play → 丢弃启动抖动帧 → 采集指定时长 → " +
                      "退出 Play → 出快照。全程由工具控制 Play 模式，你不要手动操作。\n" +
                      "如果你的需求是「自己一边操作、工具在旁边记录」，请用 perf_follow_capture_start —— " +
                      "那个才是给人玩的模式。\n" +
                      "本工具立即返回 job_id（不阻塞），用 perf_playmode_test_status 查进度、" +
                      "perf_playmode_test_result 取结论。",
        Group = "core")]
    public static class PerfPlayModeTestStart
    {
        public static object HandleCommand(JObject @params)
        {
            var job = new PlayModeTestJob();
            job.captureFrames = McpToolkit.Int(@params, "frames", PerfAgentSettings.Config.budget.captureFrames);
            job.warmupFrames = McpToolkit.Int(@params, "warmup_frames", 60);
            job.scenePath = McpToolkit.Str(@params, "scene");
            job.label = McpToolkit.Str(@params, "label");
            job.setupMethod = McpToolkit.Str(@params, "setup_method");
            job.restoreScene = McpToolkit.Bool(@params, "restore_scene", true);

            // duration_seconds > 0 → 按时长采集，frames 退化为安全上限。
            // 「跑完一整个流程」是按秒描述的；而且时长模式下实际帧数由帧率决定，
            // 所以兜底超时也得跟着放宽，否则会在流程中途被打断。
            int seconds = McpToolkit.Int(@params, "duration_seconds", 0);
            if (seconds <= 0) seconds = McpToolkit.DefaultCaptureSeconds();
            job.durationSeconds = seconds > 0 ? Math.Min((double)seconds, PerfPipeline.MaxCaptureSeconds) : 0;

            job.timeoutSeconds = McpToolkit.Int(@params, "timeout_seconds", 0);
            if (job.timeoutSeconds <= 0)
                job.timeoutSeconds = job.durationSeconds > 0 ? (job.durationSeconds + 180) : 240;

            string error;
            var started = PlayModeTestRunner.Start(job, out error);
            if (started == null) return new ErrorResponse(error);

            return new SuccessResponse(
                "已启动自动 Play 模式测试（" + started.id + "）：预热 " + started.warmupFrames
                + " 帧，再" + (started.durationSeconds > 0
                    ? string.Format(CultureInfo.InvariantCulture, "连续采集 {0:0.#} 秒", started.durationSeconds)
                    : ("采样 " + started.captureFrames + " 帧"))
                + "。整体超时上限 " + started.timeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + " 秒。",
                new
                {
                    job_id = started.id,
                    warmup_frames = started.warmupFrames,
                    capture_frames = started.captureFrames,
                    duration_seconds = started.durationSeconds,
                    timeout_seconds = started.timeoutSeconds,
                    scene = string.IsNullOrEmpty(started.scenePath) ? "(当前场景)" : started.scenePath,
                    setup_method = string.IsNullOrEmpty(started.setupMethod) ? null : started.setupMethod,
                    hint = "用 perf_playmode_test_status 轮询（建议每 2~3 秒一次）。",
                });
        }
    }

    [McpForUnityTool("perf_playmode_test_status",
        Description = "查询自动 Play 模式测试的进度：阶段、已采样帧数、百分比、耗时、事件流水。" +
                      "phase=succeeded 时用 perf_playmode_test_result 取结论。不传 job_id 时查最近一次。",
        Group = "core")]
    public static class PerfPlayModeTestStatus
    {
        public static object HandleCommand(JObject @params)
        {
            string id = McpToolkit.Str(@params, "job_id");
            var job = string.IsNullOrEmpty(id)
                ? PlayModeTestRunner.LoadCurrent()
                : PlayModeTestRunner.LoadById(id);

            if (job == null)
            {
                return new SuccessResponse("没有找到该任务。", new
                {
                    found = false,
                    job_id = id,
                    running = false,
                    hint = "用 perf_playmode_test_start 启动一次自动测试。",
                });
            }

            return new SuccessResponse(
                "阶段：" + job.PhaseText() + "（已采样 " + job.capturedFrames + "/" + job.captureFrames + " 帧）",
                new
                {
                    found = true,
                    job_id = job.id,
                    running = job.IsActive(),
                    phase = job.phase.ToString().ToLowerInvariant(),
                    phase_text = job.PhaseText(),
                    progress = Math.Round(job.Progress(), 3),
                    captured_frames = job.capturedFrames,
                    capture_frames = job.captureFrames,
                    warmup_frames = job.warmupFrames,
                    elapsed_seconds = Math.Round(job.ElapsedSeconds(), 1),
                    snapshot_id = string.IsNullOrEmpty(job.snapshotId) ? null : job.snapshotId,
                    error = string.IsNullOrEmpty(job.error) ? null : job.error,
                    events = McpToolkit.EventList(job),
                });
        }
    }

    [McpForUnityTool("perf_playmode_test_result",
        Description = "取自动 Play 模式测试的结论：性能指标、findings、是否需要修复计划。" +
                      "不传 job_id 时取最近一次；任务未完成会返回明确错误而不是空数据。",
        Group = "core")]
    public static class PerfPlayModeTestResult
    {
        public static object HandleCommand(JObject @params)
        {
            string id = McpToolkit.Str(@params, "job_id");
            var job = string.IsNullOrEmpty(id)
                ? PlayModeTestRunner.LoadCurrent()
                : PlayModeTestRunner.LoadById(id);

            if (job == null) return new ErrorResponse("没有找到该任务。先用 perf_playmode_test_status 查看。");

            if (job.IsActive())
                return new ErrorResponse("任务还在进行中（" + job.PhaseText() + "，已采样 "
                    + job.capturedFrames + "/" + job.captureFrames + " 帧）。稍后再取结果。");

            if (job.phase != PlayModeTestPhase.Succeeded || string.IsNullOrEmpty(job.snapshotId))
                return new ErrorResponse("任务未成功完成：" + job.PhaseText()
                    + (string.IsNullOrEmpty(job.error) ? "。" : " —— " + job.error));

            string snapPath;
            var snap = McpToolkit.LoadSnapshotById(job.snapshotId, out snapPath);
            if (snap == null) return new ErrorResponse("任务记录的快照文件读不到：" + job.snapshotId);

            var metrics = new List<object>();
            for (int i = 0; i < snap.metrics.Count; i++)
            {
                var m = snap.metrics[i];
                metrics.Add(new
                {
                    name = m.name,
                    value = m.value,
                    unit = m.unit,
                    budget = string.IsNullOrEmpty(m.budget) ? null : m.budget,
                    severity = m.severity,
                    samples = m.samples,
                    source = m.source,
                });
            }

            var findings = new List<object>();
            for (int i = 0; i < snap.findings.Count; i++)
                findings.Add(McpToolkit.Finding(snap.findings[i], true));

            return new SuccessResponse(
                "自动 Play 模式测试完成（" + job.id + "）：采样 " + job.capturedFrames + " 帧，"
                + findings.Count + " 条结论。",
                new
                {
                    job = job.ToDict(),
                    snapshot = McpToolkit.Brief(snap),
                    metrics = metrics,
                    findings = findings,
                    notes = snap.notes,
                    next_step = findings.Count == 0
                        ? "本次没发现超预算项。"
                        : "用 perf_get_fix_plan 取修复计划（执行入口只在 Unity 面板里）。",
                });
        }
    }

    [McpForUnityTool("perf_playmode_test_cancel",
        Description = "取消进行中的自动 Play 模式测试并退出 Play 模式。该次采样数据不会保留。" +
                      "另有兜底超时：即使不调用本工具，卡住的流程也会被强制中止。",
        Group = "core")]
    public static class PerfPlayModeTestCancel
    {
        public static object HandleCommand(JObject @params)
        {
            string reason = McpToolkit.Str(@params, "reason", "调用方主动取消。");

            string error;
            if (!PlayModeTestRunner.Cancel(reason, out error)) return new ErrorResponse(error);

            return new SuccessResponse("已取消自动测试，正在退出 Play 模式。",
                new { cancelled = true, reason = reason });
        }
    }

    // =========================================================================
    // 跟随采集：用户自己操作，工具在旁边记录
    //
    // 与 perf_playmode_test_* 的分工（两者都保留，解决不同问题）：
    //   - playmode_test_*：工具控制 Play，固定采一段时间。适合回归、版本对比、无人值守。
    //   - follow_capture_*：**不碰 Play 模式**，用户自己进、自己玩、自己退，时长不限。
    //
    // 外部 Agent 的正确用法：调 start 进入待命 → **提示用户去操作**（不要替他按 Play）
    // → 轮询 status → 用户玩完后再调 stop。
    // =========================================================================

    [McpForUnityTool("perf_follow_capture_start",
        Description = "进入「跟随采集」待命：**用户自己进 Play 操作，工具在旁边记录，时长不限**。\n" +
                      "本工具**不会**替你进 Play —— 它是给人用的模式。调用后应当让用户自己去玩，" +
                      "玩完再调 perf_follow_capture_stop 收尾。用户已经在 Play 里的话会立刻开始采集。\n" +
                      "要自动跑固定时长的测试，用 perf_playmode_test_start。",
        Group = "core")]
    public static class PerfFollowCaptureStart
    {
        public static object HandleCommand(JObject @params)
        {
            string error;
            if (!FollowCapture.Arm(out error)) return new ErrorResponse(error);

            return new SuccessResponse(
                "跟随采集已就绪。请让用户进入 Play 模式自己操作 —— 进去后会自动开始记录；"
                + "用户玩完退出 Play，或调用 perf_follow_capture_stop，即会生成快照。",
                new
                {
                    armed = FollowCapture.Armed,
                    capturing = FollowCapture.Capturing,
                    hint = "这不是自动测试 —— 别替用户按 Play，让他自己操作。",
                });
        }
    }

    [McpForUnityTool("perf_follow_capture_status",
        Description = "查询跟随采集状态：是否待命、是否在采、已记录多少帧、最近一份快照 id。",
        Group = "core")]
    public static class PerfFollowCaptureStatus
    {
        public static object HandleCommand(JObject @params)
        {
            bool capturing = FollowCapture.Capturing;
            bool armed = FollowCapture.Armed;

            return new SuccessResponse(
                capturing
                    ? ("跟随采集中：已记录 " + FollowCapture.CapturedFrames + " 帧。")
                    : (armed ? "跟随采集待命中：等用户进入 Play 模式。" : "当前没有进行中的跟随采集。"),
                new
                {
                    capturing = capturing,
                    armed = armed,
                    captured_frames = FollowCapture.CapturedFrames,
                    last_snapshot_id = string.IsNullOrEmpty(FollowCapture.LastSnapshotId)
                        ? null : FollowCapture.LastSnapshotId,
                });
        }
    }

    [McpForUnityTool("perf_follow_capture_stop",
        Description = "结束跟随采集并生成快照。**不会退出 Play 模式** —— 用户想接着玩就接着玩。\n" +
                      "返回快照 id，用 perf_get_findings / perf_get_metrics 取结论。",
        Group = "core")]
    public static class PerfFollowCaptureStop
    {
        public static object HandleCommand(JObject @params)
        {
            int captured = FollowCapture.CapturedFrames;

            string error;
            if (!FollowCapture.Stop(out error)) return new ErrorResponse(error);

            string id = FollowCapture.LastSnapshotId;

            return new SuccessResponse(
                string.IsNullOrEmpty(id)
                    ? "已取消跟随采集（还没有开始记录）。"
                    : ("跟随采集已结束，快照 " + id + " 已生成（共 " + captured + " 帧）。"),
                new
                {
                    cancelled = string.IsNullOrEmpty(id),
                    snapshot_id = string.IsNullOrEmpty(id) ? null : id,
                    captured_frames = captured,
                });
        }
    }
}

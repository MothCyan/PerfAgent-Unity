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
                    error = "当前没有快照。先调用 perf_static_audit（秒级、不进 Play），"
                          + "或让用户用面板的「跟随采集」跑一段自己操作的过程，"
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
  
        /// <summary>面板里设的默认采集时长（秒）；0 表示默认按帧数。</summary>
  
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

    // =========================================================================
    // 跟随采集：用户自己操作，工具在旁边记录
    //
    // 这是本工具集里**唯一**能拿到运行时数据的入口，而且它不碰 Play 模式：
    // 用户自己进、自己玩、自己退，时长不限。
    //
    // 外部 Agent 的正确用法：调 start 进入待命 → **提示用户去操作**（不要替他按 Play）
    // → 轮询 status → 用户玩完后再调 stop。
    // =========================================================================

    [McpForUnityTool("perf_follow_capture_start",
        Description = "进入「跟随采集」待命：**用户自己进 Play 操作，工具在旁边记录，时长不限**。\n" +
                      "本工具**不会**替你进 Play —— 它是给人用的模式。调用后应当让用户自己去玩，" +
                      "玩完再调 perf_follow_capture_stop 收尾。用户已经在 Play 里的话会立刻开始采集。",
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
                    hint = "不要替用户按 Play，让他自己操作。",
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

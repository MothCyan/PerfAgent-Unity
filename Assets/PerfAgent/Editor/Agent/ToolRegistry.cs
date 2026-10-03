using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Analysis;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Agent
{
    public class AgentTool
    {
        public string name;
        public string description;
        public string schema;                                                  // JSON Schema 文本
        public Func<Dictionary<string, object>, object> invoke;                 // 同步实现
        public Action<Dictionary<string, object>, Action<object>> invokeAsync;  // 异步实现（如抓帧）
    }

    /// <summary>
    /// Agent 工具集。
    ///
    /// 纪律（见开发计划）：
    ///  1. 工具只返回数值与标识，不做解释 —— 解释交给 LLM；
    ///  2. 每个数值都带 unit 与 source，避免歧义；
    ///  3. 所有列表类工具都必须限流（top N），原始帧数据永不进入 prompt；
    ///  4. 隐私开关生效：不允许上传源码时，代码片段会被剥离。
    /// </summary>
    public static class ToolRegistry
    {
        public static List<AgentTool> All()
        {
            var list = new List<AgentTool>();
            list.Add(Tool("list_snapshots", "列出本地已有的性能快照（id / 标签 / 采集时间）。", Schema(""),
                delegate (Dictionary<string, object> a) { return ListSnapshots(); }));

            list.Add(Tool("load_snapshot", "加载指定快照作为当前分析对象。",
                Schema(P("snapshot_id", "string", "快照 id（文件名去掉 .json）"), "\"snapshot_id\""),
                delegate (Dictionary<string, object> a) { return LoadSnapshot(MiniJson.Str(a, "snapshot_id")); }));

            list.Add(AsyncTool("capture_frames", "在编辑器内采集 N 帧运行时数据（帧耗时、分配、GC、Draw Call、TempAllocator），采集会自动附带全部静态审计与规则分析。耗时约等于 N 帧的真实时间。",
                Schema(P("frames", "integer", "采样帧数，默认取配置值（300）"), ""),
                delegate (Dictionary<string, object> a, Action<object> complete) { CaptureFrames(a, complete); }));

            list.Add(Tool("get_summary", "获取当前快照的整体摘要：环境、关键指标、结论概览、问题数量。这是排查的第一步，用来决定后续深入哪个方向。",
                Schema(""), delegate (Dictionary<string, object> a) { return Summary(); }));

            list.Add(Tool("get_metrics", "获取当前快照的全部标量指标（含预算对比与级别）。",
                Schema(P("filter", "string", "可选的名称子串过滤，如 \"Draw\"")),
                delegate (Dictionary<string, object> a) { return Metrics(MiniJson.Str(a, "filter")); }));

            list.Add(Tool("get_frames", "获取帧级数据：最慢的帧、尖峰帧号、与平均帧的差异。用于判断卡顿是「稳态慢」还是「尖峰卡」。",
                Schema(P("top", "integer", "返回最慢的前 N 帧，默认 8")),
                delegate (Dictionary<string, object> a) { return Frames(MiniJson.Int(a, "top", 8)); }));

            list.Add(Tool("get_markers", "获取 Profiler Top Marker（自身耗时 / GC 分配 / 调用次数）。数据来自 Profiler 层级视图，版本差异较大，可能为空。",
                Schema(P("top", "integer", "返回前 N 条，默认 15")),
                delegate (Dictionary<string, object> a) { return Markers(MiniJson.Int(a, "top", 15)); }));

            list.Add(Tool("get_findings", "获取规则引擎产出的诊断结论（含完整证据链、置信度与建议）。这是最权威的结论来源，请优先引用它而不是自己推断。",
                Schema(P("severity", "string", "可选过滤：error / warn / info")),
                delegate (Dictionary<string, object> a) { return Findings(MiniJson.Str(a, "severity")); }));

            list.Add(Tool("get_asset_issues", "获取资源导入问题列表（纹理/模型/音频/Resources 滥用），按严重度排序。",
                Schema(P("top", "integer", "返回条数，默认 20") + "," + P("type", "string", "可选资源类型过滤：Texture / Model / Audio / Resources / Large")),
                delegate (Dictionary<string, object> a) { return AssetIssues(MiniJson.Int(a, "top", 20), MiniJson.Str(a, "type")); }));

            list.Add(Tool("get_scene_issues", "获取场景与物理层面的问题（组件反模式、批处理漏网、物理配置）。",
                Schema(P("top", "integer", "返回条数，默认 20") + "," + P("component", "string", "可选组件类型过滤，如 SkinnedMeshRenderer")),
                delegate (Dictionary<string, object> a) { return SceneIssues(MiniJson.Int(a, "top", 20), MiniJson.Str(a, "component")); }));

            list.Add(Tool("get_code_issues", "获取脚本反模式扫描结果（每帧方法体内的堆分配、查找类 API、LINQ 等）。",
                Schema(P("top", "integer", "返回条数，默认 25") + "," + P("pattern", "string", "可选的模式名过滤，如 linq")),
                delegate (Dictionary<string, object> a) { return CodeIssues(MiniJson.Int(a, "top", 25), MiniJson.Str(a, "pattern")); }));

            list.Add(Tool("rerun_audit", "重新运行某个审计采集器（不重新抓帧），用于在修改资源/代码后验证问题是否消失。",
                Schema(P("collector", "string", "采集器工具名：audit_assets / audit_scene / audit_physics / scan_scripts / memory / render_stats / env_info / frame_timing / profile_markers") + "," +
                       P("deep", "boolean", "是否深度扫描（更慢但覆盖更全）"), "\"collector\""),
                delegate (Dictionary<string, object> a) { return RerunAudit(MiniJson.Str(a, "collector"), MiniJson.Bool(a, "deep")); }));

            list.Add(Tool("run_rules", "在保持数据不变的前提下重新运行规则引擎（例如刚改过预算阈值）。",
                Schema(""), delegate (Dictionary<string, object> a) { return RunRules(); }));

            list.Add(Tool("diff_snapshots", "对比两个快照并给出确定性判定：regressed_metrics（变差）/ improved_metrics（变好）已按指标方向分好类，verdict 为整体结论（明显改善 / 轻微改善 / 基本持平 / 轻微恶化 / 明显恶化），同时列出新增与消除的结论。用于验证优化是否生效或定位性能回归。",
                Schema(P("baseline_id", "string", "作为基准的快照 id") + "," + P("current_id", "string", "作为当前的快照 id；留空表示当前快照"), "\"baseline_id\""),
                delegate (Dictionary<string, object> a) { return Diff(MiniJson.Str(a, "baseline_id"), MiniJson.Str(a, "current_id")); }));

            list.Add(Tool("get_budget", "获取当前性能预算（诊断的判定标准）。", Schema(""),
                delegate (Dictionary<string, object> a) { return Budget(); }));

            list.Add(Tool("get_fix_plan", "获取当前快照的修复计划：每条结论对应可一键执行的修复动作（含 action_id、风险等级、影响目标数、是否可撤销）与必须人工处理的步骤。重要：本工具只返回计划，真正的修改必须由用户在面板里点按钮确认，你不能自行调用任何修改动作。",
                Schema(P("finding_id", "string", "可选：只看某条结论的修复计划") + "," +
                       P("executable_only", "boolean", "只返回含可执行动作的计划，默认 true")),
                delegate (Dictionary<string, object> a) { return FixPlan(MiniJson.Str(a, "finding_id"), MiniJson.Bool(a, "executable_only", true)); }));

            list.Add(Tool("set_budget", "修改性能预算（例如把目标帧率改成 30、把 Draw Call 预算改成 150）。修改后应重新调用 run_rules。",
                Schema(
                    P("target_frame_rate", "number", "目标帧率") + "," +
                    P("max_draw_calls", "integer", "Draw Call 预算") + "," +
                    P("max_set_pass_calls", "integer", "SetPass Call 预算") + "," +
                    P("max_triangles", "integer", "三角面预算") + "," +
                    P("max_managed_alloc_bytes_per_frame", "integer", "每帧托管分配预算（字节，0 表示零容忍）") + "," +
                    P("max_texture_memory_mb", "integer", "纹理内存预算 (MB)") + "," +
                    P("max_temp_allocator_mb", "integer", "Temp Allocator 预算 (MB)")),
                delegate (Dictionary<string, object> a) { return SetBudget(a); }));

            list.Add(Tool("export_report", "把当前快照导出为 Markdown 或 HTML 报告，返回文件路径。",
                Schema(P("format", "string", "md 或 html，默认 md")),
                delegate (Dictionary<string, object> a) { return ExportReport(MiniJson.Str(a, "format", "md")); }));

            list.Add(Tool("probe_api", "探测当前 Unity 版本下 Profiler / 内存 / 渲染相关 API 的可用性。当某个工具返回空数据时可先用它确认环境能力。",
                Schema(""), delegate (Dictionary<string, object> a) { return Probe(); }));

            return list;
        }

        /// <summary>解析后的工具定义数组，可直接放进 OpenAI 请求体。</summary>
        public static List<object> Definitions()
        {
            var all = All();
            var result = new List<object>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                var fn = new Dictionary<string, object>();
                fn["name"] = t.name;
                fn["description"] = t.description;
                fn["parameters"] = MiniJson.ParseSafe(t.schema);

                var item = new Dictionary<string, object>();
                item["type"] = "function";
                item["function"] = fn;
                result.Add(item);
            }
            return result;
        }

        public static AgentTool Find(string name)
        {
            var all = All();
            for (int i = 0; i < all.Count; i++) if (all[i].name == name) return all[i];
            return null;
        }

        // =====================================================================
        // 工具实现
        // =====================================================================

        static object ListSnapshots()
        {
            var paths = PerfSnapshotStore.List();
            var items = new List<object>();
            for (int i = 0; i < paths.Count && i < 40; i++)
            {
                string file = System.IO.Path.GetFileNameWithoutExtension(paths[i]);
                var d = new Dictionary<string, object>();
                d["snapshot_id"] = file;
                d["captured_utc"] = System.IO.File.GetLastWriteTimeUtc(paths[i]).ToString("o", CultureInfo.InvariantCulture);
                d["is_current"] = file == CurrentId();
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["count"] = paths.Count;
            r["snapshots"] = items;
            return r;
        }

        static object LoadSnapshot(string id)
        {
            if (string.IsNullOrEmpty(id)) return Error("缺少 snapshot_id");
            var paths = PerfSnapshotStore.List();
            for (int i = 0; i < paths.Count; i++)
            {
                if (System.IO.Path.GetFileNameWithoutExtension(paths[i]) == id)
                {
                    var snap = PerfSnapshotStore.Load(paths[i]);
                    if (snap == null) return Error("快照读取失败: " + id);
                    PerfPipeline.Analyze(snap);
                    PerfSession.SetCurrent(snap, paths[i]);
                    return Summary();
                }
            }
            return Error("未找到快照: " + id);
        }

        static void CaptureFrames(Dictionary<string, object> args, Action<object> complete)
        {
            if (PerfSession.Capturing)
            {
                complete(Error("已有抓帧任务正在进行，请稍后重试。"));
                return;
            }

            int frames = MiniJson.Int(args, "frames", PerfAgentSettings.Config.budget.captureFrames);
            if (frames < 10) frames = 10;
            if (frames > PerfPipeline.MaxCaptureFrames) frames = PerfPipeline.MaxCaptureFrames;

            // seconds > 0 时按时间采，frames 退化为安全上限 ——
            // 「跑完一整个流程」这类需求是按秒描述的
            int seconds = MiniJson.Int(args, "seconds", 0);
            if (seconds > 0)
            {
                double duration = Math.Min((double)seconds, PerfPipeline.MaxCaptureSeconds);
                PerfPipeline.CaptureFrames(frames, duration,
                    delegate (PerfSnapshot snap) { complete(Summary()); }, null,
                    string.Format(CultureInfo.InvariantCulture, "Agent 抓帧 {0:0.#}s", duration));
                return;
            }

            PerfPipeline.CaptureFrames(frames, delegate (PerfSnapshot snap) { complete(Summary()); }, null, "Agent 抓帧");
        }

        static object RequireSnapshot()
        {
            if (PerfSession.Current == null) return Error("当前没有快照。请先调用 capture_frames，或 load_snapshot 加载已有快照。");
            return null;
        }

        static object Summary()
        {
            var snap = PerfSession.Current;
            if (snap == null) return Error("当前没有快照。");

            var r = new Dictionary<string, object>();
            r["snapshot_id"] = snap.id;
            r["captured_utc"] = snap.capturedUtc;
            r["sampled_frames"] = snap.frames.Count;

            var env = new Dictionary<string, object>();
            env["unity"] = snap.unityVersion;
            env["platform"] = snap.platform + " / " + snap.buildTarget;
            env["graphics_device"] = snap.graphicsDevice;
            env["render_pipeline"] = snap.renderPipeline;
            env["scene"] = snap.scenePath;
            env["target_frame_rate"] = snap.targetFrameRate;
            env["v_sync"] = snap.vSyncCount;
            env["quality"] = snap.qualityName;
            env["fixed_delta_time"] = snap.fixedDeltaTime;
            r["environment"] = env;

            var metrics = new List<object>();
            for (int i = 0; i < snap.metrics.Count; i++)
            {
                var m = snap.metrics[i];
                var d = new Dictionary<string, object>();
                d["name"] = m.name;
                d["value"] = m.value;
                d["unit"] = m.unit;
                if (!string.IsNullOrEmpty(m.budget)) d["budget"] = m.budget;
                d["severity"] = m.severity;
                d["source"] = m.source;
                metrics.Add(d);
            }
            r["metrics"] = metrics;

            var findings = new List<object>();
            for (int i = 0; i < snap.findings.Count; i++)
            {
                var f = snap.findings[i];
                var d = new Dictionary<string, object>();
                d["severity"] = f.severity;
                d["category"] = f.category;
                d["title"] = f.title;
                d["confidence"] = f.confidence;
                d["evidence_count"] = f.evidence.Count;
                findings.Add(d);
            }
            r["findings"] = findings;

            var counts = new Dictionary<string, object>();
            counts["asset_issues"] = snap.assetIssues.Count;
            counts["scene_issues"] = snap.sceneIssues.Count;
            counts["code_issues"] = snap.codeIssues.Count;
            counts["markers"] = snap.markers.Count;
            r["issue_counts"] = counts;

            if (snap.notes.Count > 0)
            {
                var notes = new List<object>();
                for (int i = 0; i < snap.notes.Count && i < 10; i++) notes.Add(snap.notes[i]);
                r["notes"] = notes;
            }
            r["hint"] = "如需深入，请按 findings 的分类调用对应工具：渲染看 get_metrics/get_markers，内存看 get_metrics + get_asset_issues，CPU 看 get_markers，代码看 get_code_issues。";
            return r;
        }

        static object Metrics(string filter)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            var items = new List<object>();
            for (int i = 0; i < snap.metrics.Count; i++)
            {
                var m = snap.metrics[i];
                if (!string.IsNullOrEmpty(filter) && m.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var d = new Dictionary<string, object>();
                d["name"] = m.name;
                d["value"] = m.value;
                d["unit"] = m.unit;
                d["source"] = m.source;
                d["severity"] = m.severity;
                if (!string.IsNullOrEmpty(m.budget)) { d["budget"] = m.budget; d["budget_unit"] = m.budgetUnit; }
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["snapshot_id"] = snap.id;
            r["metrics"] = items;
            return r;
        }

        static object Frames(int top)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            if (snap.frames.Count == 0) return Error("当前快照没有帧数据（只做了静态审计）。请调用 capture_frames。");

            var r = new Dictionary<string, object>();
            r["sampled_frames"] = snap.frames.Count;
            r["avg_ms"] = snap.FrameTimeAvgMs();
            r["p50_ms"] = snap.FrameTimePercentileMs(50);
            r["p95_ms"] = snap.FrameTimePercentileMs(95);
            r["max_ms"] = snap.FrameTimeMaxMs();
            r["avg_managed_alloc_bytes_per_frame"] = snap.AvgManagedAllocBytesPerFrame();
            r["gc_events"] = snap.GcEventCount();

            var spikes = snap.SpikeFrames(10);
            var spikeList = new List<object>();
            for (int i = 0; i < spikes.Count; i++) spikeList.Add(spikes[i]);
            r["spike_frames"] = spikeList;

            var frames = new List<FrameStat>(snap.frames);
            frames.Sort(delegate (FrameStat a, FrameStat b) { return b.deltaMs.CompareTo(a.deltaMs); });
            var slowest = new List<object>();
            for (int i = 0; i < frames.Count && i < top; i++)
            {
                var f = frames[i];
                var d = new Dictionary<string, object>();
                d["frame"] = f.frame;
                d["delta_ms"] = f.deltaMs;
                d["managed_alloc_bytes"] = f.managedAllocBytes;
                d["temp_alloc_bytes"] = f.tempAllocBytes;
                d["draw_calls"] = f.drawCalls;
                d["set_pass_calls"] = f.setPassCalls;
                d["triangles"] = f.triangles;
                slowest.Add(d);
            }
            r["slowest_frames"] = slowest;
            return r;
        }

        static object Markers(int top)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            var items = new List<object>();
            for (int i = 0; i < snap.markers.Count && i < top; i++)
            {
                var m = snap.markers[i];
                var d = new Dictionary<string, object>();
                d["name"] = m.name;
                d["self_ms"] = m.selfMs;
                d["total_ms"] = m.totalMs;
                d["gc_alloc_bytes"] = m.gcAllocBytes;
                d["calls"] = m.calls;
                d["depth"] = m.depth;
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["count"] = snap.markers.Count;
            r["markers"] = items;
            if (items.Count == 0) r["note"] = "无 marker 数据（该 Unity 版本的层级视图 API 形态可能不同，可调用 probe_api 确认）。";
            return r;
        }

        static object Findings(string severity)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            var items = new List<object>();
            for (int i = 0; i < snap.findings.Count; i++)
            {
                var f = snap.findings[i];
                if (!string.IsNullOrEmpty(severity) && f.severity != severity) continue;

                var d = new Dictionary<string, object>();
                d["severity"] = f.severity;
                d["category"] = f.category;
                d["title"] = f.title;
                d["detail"] = f.detail;
                d["recommendation"] = f.recommendation;
                d["confidence"] = f.confidence;
                if (!string.IsNullOrEmpty(f.jumpTo)) d["jump_to"] = f.jumpTo;

                var evs = new List<object>();
                for (int k = 0; k < f.evidence.Count; k++)
                {
                    var e = f.evidence[k];
                    var ed = new Dictionary<string, object>();
                    ed["tool"] = e.tool;
                    ed["metric"] = e.metric;
                    ed["value"] = e.value;
                    ed["unit"] = e.unit;
                    if (!string.IsNullOrEmpty(e.threshold)) ed["threshold"] = e.threshold;
                    if (!string.IsNullOrEmpty(e.source)) ed["source"] = e.source;
                    evs.Add(ed);
                }
                d["evidence"] = evs;
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["count"] = items.Count;
            r["findings"] = items;
            return r;
        }

        static object AssetIssues(int top, string type)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            var items = new List<object>();
            for (int i = 0; i < snap.assetIssues.Count; i++)
            {
                var a = snap.assetIssues[i];
                if (!string.IsNullOrEmpty(type) && !string.Equals(a.assetType, type, StringComparison.OrdinalIgnoreCase)) continue;
                if (items.Count >= top) break;

                var d = new Dictionary<string, object>();
                d["path"] = FilterPath(a.path);
                d["type"] = a.assetType;
                d["issue"] = a.issue;
                d["suggestion"] = a.suggestion;
                d["severity"] = a.severity;
                d["disk_bytes"] = a.diskBytes;
                if (a.estimatedMemoryBytes > 0) d["estimated_memory_bytes"] = a.estimatedMemoryBytes;
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["total"] = snap.assetIssues.Count;
            r["returned"] = items.Count;
            r["issues"] = items;
            return r;
        }

        static object SceneIssues(int top, string component)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            var items = new List<object>();
            for (int i = 0; i < snap.sceneIssues.Count; i++)
            {
                var v = snap.sceneIssues[i];
                if (!string.IsNullOrEmpty(component) && !string.Equals(v.componentType, component, StringComparison.OrdinalIgnoreCase)) continue;
                if (items.Count >= top) break;

                var d = new Dictionary<string, object>();
                d["path"] = v.hierarchyPath;
                d["component"] = v.componentType;
                d["issue"] = v.issue;
                d["suggestion"] = v.suggestion;
                d["severity"] = v.severity;
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["total"] = snap.sceneIssues.Count;
            r["returned"] = items.Count;
            r["issues"] = items;
            return r;
        }

        static object CodeIssues(int top, string pattern)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            bool allowPaths = PerfAgentSettings.Config.allowAssetPathUpload;
            bool allowSource = PerfAgentSettings.Config.allowSourceCodeUpload;

            var items = new List<object>();
            for (int i = 0; i < snap.codeIssues.Count; i++)
            {
                var c = snap.codeIssues[i];
                if (!string.IsNullOrEmpty(pattern) && c.pattern.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (items.Count >= top) break;

                var d = new Dictionary<string, object>();
                d["location"] = allowPaths ? (c.file + ":" + c.line) : System.IO.Path.GetFileName(c.file) + ":" + c.line;
                d["pattern"] = c.pattern;
                d["suggestion"] = c.suggestion;
                d["severity"] = c.severity;
                d["snippet"] = allowSource ? c.snippet : "(已按隐私设置隐藏)";
                items.Add(d);
            }
            var r = new Dictionary<string, object>();
            r["total"] = snap.codeIssues.Count;
            r["returned"] = items.Count;
            r["issues"] = items;
            return r;
        }

        static object RerunAudit(string collector, bool deep)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            if (string.IsNullOrEmpty(collector)) return Error("缺少 collector 参数");

            var snap = PerfSession.Current;
            // 清掉该采集器上次的产出，避免重复累积
            switch (collector)
            {
                case "audit_assets": snap.assetIssues.Clear(); break;
                case "audit_scene":
                case "audit_physics": snap.sceneIssues.Clear(); break;
                case "scan_scripts": snap.codeIssues.Clear(); break;
            }

            PerfPipeline.RunCollectors(snap, deep, null, collector);
            PerfPipeline.Analyze(snap);
            PerfPipeline.SaveAndSetCurrent(snap);

            var r = new Dictionary<string, object>();
            r["collector"] = collector;
            r["asset_issues"] = snap.assetIssues.Count;
            r["scene_issues"] = snap.sceneIssues.Count;
            r["code_issues"] = snap.codeIssues.Count;
            r["findings"] = snap.findings.Count;
            r["note"] = "已重新运行并与规则引擎结果合并。";
            return r;
        }

        static object RunRules()
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            var snap = PerfSession.Current;
            PerfPipeline.Analyze(snap);
            PerfPipeline.SaveAndSetCurrent(snap);
            return Findings(null);
        }

        static object Diff(string baselineId, string currentId)
        {
            if (string.IsNullOrEmpty(baselineId)) return Error("缺少 baseline_id");
            if (PerfSession.Current == null && string.IsNullOrEmpty(currentId)) return Error("缺少 current_id，且当前没有快照。");

            PerfSnapshot baseline = null, current = null;
            var paths = PerfSnapshotStore.List();
            for (int i = 0; i < paths.Count; i++)
            {
                string id = System.IO.Path.GetFileNameWithoutExtension(paths[i]);
                if (id == baselineId) baseline = PerfSnapshotStore.Load(paths[i]);
                if (!string.IsNullOrEmpty(currentId) && id == currentId) current = PerfSnapshotStore.Load(paths[i]);
            }
            if (current == null) current = PerfSession.Current;
            if (baseline == null) return Error("未找到基准快照: " + baselineId);
            if (current == null) return Error("未找到当前快照。");

            // 同时把基准值写回指标，便于报告里直接展示「值 / 上次值」
            PerfSnapshotStore.Diff(baseline, current);

            var diff = PerfDiff.Compare(baseline, current);

            var r = new Dictionary<string, object>();
            r["baseline_id"] = diff.baselineId;
            r["current_id"] = diff.currentId;
            if (!diff.Comparable) { r["error"] = diff.error; return r; }

            // 「变好 / 变坏」由方向规则判定后直接给出，不允许模型自行推断正负号含义
            r["verdict"] = diff.Verdict();
            r["severity_score"] = diff.Score();
            r["regressed_metric_count"] = diff.RegressedMetricCount;
            r["improved_metric_count"] = diff.ImprovedMetricCount;

            var regressed = new List<object>();
            var improved = new List<object>();
            for (int i = 0; i < diff.metrics.Count; i++)
            {
                var m = diff.metrics[i];
                if (m.changed) (m.regressed ? regressed : improved).Add(MetricDeltaJson(m));
            }
            r["regressed_metrics"] = regressed;
            r["improved_metrics"] = improved;

            r["new_findings"] = FindingBriefs(diff.newFindings);
            r["resolved_findings"] = FindingBriefs(diff.resolvedFindings);
            r["worsened_findings"] = FindingBriefs(diff.worsenedFindings);
            r["improved_findings"] = FindingBriefs(diff.improvedFindings);
            r["persisting_finding_count"] = diff.persistingFindings.Count;

            r["new_asset_issues"] = Count(diff.newAssetIssues);
            r["resolved_asset_issues"] = Count(diff.resolvedAssetIssues);
            r["new_scene_issues"] = Count(diff.newSceneIssues);
            r["resolved_scene_issues"] = Count(diff.resolvedSceneIssues);
            r["new_code_issues"] = Count(diff.newCodeIssues);
            r["resolved_code_issues"] = Count(diff.resolvedCodeIssues);

            r["note"] = "regressed_metrics = 相对基准变差，improved_metrics = 相对基准变好；" +
                        "变化幅度不足 " + (PerfDiff.SignificanceThreshold * 100).ToString("0.#", CultureInfo.InvariantCulture) +
                        "% 的指标视为噪声，不出现在这两个列表里。";
            return r;
        }

        static Dictionary<string, object> MetricDeltaJson(MetricDelta m)
        {
            var d = new Dictionary<string, object>();
            d["name"] = m.name;
            d["unit"] = m.unit;
            d["direction"] = m.direction;   // cost = 越小越好，benefit = 越大越好
            d["baseline"] = m.baseline;
            d["current"] = m.current;
            d["delta"] = m.delta;
            d["delta_percent"] = m.deltaPercent;
            d["baseline_severity"] = m.baselineSeverity;
            d["current_severity"] = m.currentSeverity;
            return d;
        }

        static List<object> FindingBriefs(List<PerfFinding> findings)
        {
            var list = new List<object>(findings.Count);
            for (int i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                var d = new Dictionary<string, object>();
                d["id"] = f.id;
                d["category"] = f.category;
                d["severity"] = f.severity;
                d["title"] = f.title;
                list.Add(d);
            }
            return list;
        }

        static int Count<T>(List<T> list) { return list == null ? 0 : list.Count; }

        /// <summary>
        /// 只读工具：把修复计划交给 Agent 解释与排优先级。
        /// 它不会、也不能执行任何修改 —— 执行入口只存在于面板上，且必须用户点击确认。
        /// </summary>
        static object FixPlan(string findingId, bool executableOnly)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;

            var plans = PerfFixPlanner.BuildPlans(PerfSession.Current);
            var list = new List<object>();

            for (int i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                if (!string.IsNullOrEmpty(findingId) && p.findingId != findingId) continue;
                if (executableOnly && !p.HasExecutable) continue;

                var d = new Dictionary<string, object>();
                d["finding_id"] = p.findingId;
                d["category"] = p.category;
                d["severity"] = p.severity;
                d["title"] = p.title;
                d["expected_gain"] = p.expectedGain;
                d["executable_step_count"] = p.ExecutableStepCount;
                d["low_risk_step_count"] = p.SafeStepCount;

                var steps = new List<object>();
                for (int k = 0; k < p.steps.Count; k++)
                {
                    var s = p.steps[k];
                    var sd = new Dictionary<string, object>();
                    sd["action_id"] = s.actionId;
                    sd["kind"] = s.kind;
                    sd["risk"] = s.risk;
                    sd["executable"] = s.CanExecute;
                    sd["title"] = s.title;
                    sd["what_it_changes"] = s.detail;
                    sd["target_count"] = s.targetCount;
                    sd["reversible"] = s.reversible;

                    var sample = new List<object>();
                    for (int t = 0; t < s.targets.Count && t < 5; t++) sample.Add(s.targets[t]);
                    if (sample.Count > 0) sd["targets_sample"] = sample;

                    steps.Add(sd);
                }

                d["steps"] = steps;
                list.Add(d);
            }

            var r = new Dictionary<string, object>();
            r["plans"] = list;
            r["count"] = list.Count;
            r["note"] = "计划由确定性规划器生成，不是模型推断。risk：safe 只影响内存/开销，moderate 会改变资源导入结果或运行时行为，"
                      + "risky 可能影响玩法。执行入口在面板「结论」标签下每条结论的修复计划里，必须由用户点击并确认。";
            return r;
        }

        static object Budget()
        {
            var b = PerfAgentSettings.Config.budget;
            var r = new Dictionary<string, object>();
            r["target_frame_rate"] = b.targetFrameRate;
            r["frame_budget_ms"] = b.FrameBudgetMs();
            r["warn_ratio"] = b.warnRatio;
            r["max_managed_alloc_bytes_per_frame"] = b.maxManagedAllocBytesPerFrame;
            r["max_draw_calls"] = b.maxDrawCalls;
            r["max_set_pass_calls"] = b.maxSetPassCalls;
            r["max_triangles"] = b.maxTriangles;
            r["max_texture_memory_mb"] = b.maxTextureMemoryMB;
            r["max_total_memory_mb"] = b.maxTotalMemoryMB;
            r["max_temp_allocator_mb"] = b.maxTempAllocatorMB;
            r["max_realtime_shadow_lights"] = b.maxRealtimeShadowLights;
            r["max_audio_source_count"] = b.maxAudioSourceCount;
            r["capture_frames"] = b.captureFrames;
            return r;
        }

        static object SetBudget(Dictionary<string, object> a)
        {
            var b = PerfAgentSettings.Config.budget;
            var changed = new List<object>();

            if (a.ContainsKey("target_frame_rate")) { b.targetFrameRate = (float)MiniJson.Num(a, "target_frame_rate", b.targetFrameRate); changed.Add("target_frame_rate"); }
            if (a.ContainsKey("max_draw_calls")) { b.maxDrawCalls = (int)MiniJson.Num(a, "max_draw_calls", b.maxDrawCalls); changed.Add("max_draw_calls"); }
            if (a.ContainsKey("max_set_pass_calls")) { b.maxSetPassCalls = (int)MiniJson.Num(a, "max_set_pass_calls", b.maxSetPassCalls); changed.Add("max_set_pass_calls"); }
            if (a.ContainsKey("max_triangles")) { b.maxTriangles = (long)MiniJson.Num(a, "max_triangles", b.maxTriangles); changed.Add("max_triangles"); }
            if (a.ContainsKey("max_managed_alloc_bytes_per_frame")) { b.maxManagedAllocBytesPerFrame = (long)MiniJson.Num(a, "max_managed_alloc_bytes_per_frame", b.maxManagedAllocBytesPerFrame); changed.Add("max_managed_alloc_bytes_per_frame"); }
            if (a.ContainsKey("max_texture_memory_mb")) { b.maxTextureMemoryMB = (long)MiniJson.Num(a, "max_texture_memory_mb", b.maxTextureMemoryMB); changed.Add("max_texture_memory_mb"); }
            if (a.ContainsKey("max_temp_allocator_mb")) { b.maxTempAllocatorMB = (long)MiniJson.Num(a, "max_temp_allocator_mb", b.maxTempAllocatorMB); changed.Add("max_temp_allocator_mb"); }

            PerfAgentSettings.Config.Save();
            var r = new Dictionary<string, object>();
            r["changed"] = changed;
            r["budget"] = Budget();
            r["note"] = "预算已更新。请调用 run_rules 重新评估现有数据。";
            return r;
        }

        static object ExportReport(string format)
        {
            var guard = RequireSnapshot(); if (guard != null) return guard;
            bool html = string.Equals(format, "html", StringComparison.OrdinalIgnoreCase);
            string path = PerfAgent.Analysis.PerfReportExporter.Save(PerfSession.Current, html);
            var r = new Dictionary<string, object>();
            r["path"] = path;
            r["format"] = html ? "html" : "md";
            return r;
        }

        static object Probe()
        {
            var r = new Dictionary<string, object>();
            r["profiler_api"] = ProfilerApi.DescribeAvailability();
            r["profiler_enabled"] = ProfilerApi.Enabled;
            r["first_frame_index"] = ProfilerApi.FirstFrameIndex;
            r["last_frame_index"] = ProfilerApi.LastFrameIndex;
            r["can_read_frames"] = ProfilerApi.CanReadFrames;

            var mem = new Dictionary<string, object>();
            string[] names = { "GetTotalAllocatedMemoryLong", "GetTotalReservedMemoryLong", "GetMonoUsedSizeLong", "GetMonoHeapSizeLong", "GetTempAllocatorSize", "GetAllocatedMemoryForGraphicsDriver", "GetRuntimeMemorySizeLong" };
            for (int i = 0; i < names.Length; i++) mem[names[i]] = MemApi.Has(names[i]);
            r["memory_api"] = mem;

            var stats = new Dictionary<string, object>();
            string[] statNames = { "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count", "Texture Count", "Mesh Count", "Material Count", "Total Used Memory", "Object Count" };
            for (int i = 0; i < statNames.Length; i++)
            {
                var rec = Collectors.StatRecorder.Make(Unity.Profiling.ProfilerCategory.Render, statNames[i]);
                bool valid = rec.Valid;
                if (valid) { try { rec.Dispose(); } catch { } }
                stats[statNames[i]] = valid;
            }
            r["profiler_recorder"] = stats;
            r["unity_stats"] = Reflect.FindType("UnityEditorInternal.UnityStats") != null;
            r["frame_timing_manager"] = true;
            return r;
        }

        // =====================================================================
        // 构造与辅助
        // =====================================================================

        static AgentTool Tool(string name, string description, string schema, Func<Dictionary<string, object>, object> impl)
        {
            var t = new AgentTool();
            t.name = name;
            t.description = description;
            t.schema = schema;
            t.invoke = impl;
            return t;
        }

        static AgentTool AsyncTool(string name, string description, string schema, Action<Dictionary<string, object>, Action<object>> impl)
        {
            var t = new AgentTool();
            t.name = name;
            t.description = description;
            t.schema = schema;
            t.invokeAsync = impl;
            return t;
        }

        static string Schema(string properties, string required = "")
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"object\",\"properties\":{").Append(properties).Append('}');
            if (!string.IsNullOrEmpty(required)) sb.Append(",\"required\":[").Append(required).Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        static string P(string name, string type, string description)
        {
            return "\"" + name + "\":{\"type\":\"" + type + "\",\"description\":" + MiniJson.Serialize(description) + "}";
        }

        static Dictionary<string, object> Error(string message)
        {
            var d = new Dictionary<string, object>();
            d["error"] = message;
            return d;
        }

        static string CurrentId()
        {
            return PerfSession.Current == null ? "" : PerfSession.Current.id;
        }

        static string FilterPath(string path)
        {
            if (PerfAgentSettings.Config.allowAssetPathUpload) return path;
            return System.IO.Path.GetFileName(path) + " (路径已按隐私设置隐藏)";
        }
    }
}

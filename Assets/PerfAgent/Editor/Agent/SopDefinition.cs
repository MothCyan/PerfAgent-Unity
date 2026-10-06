using System;
using System.Collections.Generic;
using System.Text;
using PerfAgent.Core;

namespace PerfAgent.Agent
{
    /// <summary>
    /// AI 的 SOP 阶段（标准操作流程）。
    ///
    /// <para><b>为什么必须把 SOP 写成代码而不是提示词里的一段话</b></para>
    /// 「AI 不可能不出错」是前提，所以流程不能靠模型自觉：
    ///   · 每个阶段**只允许调这个阶段的工具**（<see cref="ToolsFor"/> 真的会卡住越界调用）；
    ///   · 每个阶段的产出必须能落到日志里（分析目录 / 操作日志，见 <see cref="PerfHistory"/>）；
    ///   · 每条硬规则（<see cref="HardRules"/>）都能被复核，而不是「提示词里写过」。
    /// </summary>
    public enum SopPhase
    {
        /// <summary>还没确定目标。</summary>
        Idle = 0,
        /// <summary>S1 目标确认：用户想解决什么（掉帧/卡顿/内存涨），范围与场景。</summary>
        Goal,
        /// <summary>S2 采集：**由人操作**（工具只提示，不替用户按 Play）。</summary>
        Capture,
        /// <summary>S3 只读分析：拿数据、跑规则、看既有证据。</summary>
        Analyze,
        /// <summary>S4 提方案：把候选动作列出来，每条带目标/影响面/风险/回滚。</summary>
        Propose,
        /// <summary>S5 等同意：**门禁阶段**，任何改工程的动作在这里都必须停下来等人。</summary>
        AwaitConsent,
        /// <summary>S6 执行：只有拿到同意凭据才能进入（外部客户端走 MCP 时也要带凭据）。</summary>
        Execute,
        /// <summary>S7 验证：重跑审计 / 对比快照，确认真的变好了。</summary>
        Verify,
        /// <summary>S8 交付：导出 / 复制，并把结论与过程一并交代清楚。</summary>
        Deliver
    }

    /// <summary>
    /// SOP 的定义（阶段 → 允许的工具 + 硬规则 + 注入模型的流程文本）。
    /// 改流程只改这一个文件，面板、Agent、MCP 桥接都从它读。
    /// </summary>
    public static class SopDefinition
    {
        public const string Version = "1.0";

        /// <summary>
        /// 硬规则：这些不是「尽量」，是被工具与门禁强制执行的。
        /// 面板里会原样显示给用户看，方便他核对 AI 有没有照做。
        /// </summary>
        public static readonly string[] HardRules =
        {
            "R1 没有证据不下结论：每个数值都必须来自工具返回值，禁止估算或补全。",
            "R2 数据不足时写「无法归因」，不给一个偏小的数充当结论。",
            "R3 改工程之前必须先给出：目标、影响面、风险等级、回滚方式。",
            "R4 没有人工同意不得执行；同意来源必须写进操作日志（consent 字段）。",
            "R5 执行后必须验证（重跑审计或对比快照），并把结果写回日志。",
            "R6 失败要如实报告，不许用重试掩盖；改一半的状态必须能撤销。",
            "R7 原始帧数据不进上下文，只引用快照 id 与指标。",
            "R8 隐私开关生效：不允许外传时不发源码片段与绝对路径。"
        };

        /// <summary>阶段 → 允许调用的工具（空数组 = 该阶段不该调工具）。</summary>
        public static string[] ToolsFor(SopPhase phase)
        {
            switch (phase)
            {
                case SopPhase.Goal:
                    return new[] { "list_snapshots", "load_snapshot", "get_budget" };

                // 采集必须由人完成：工具没有「替你进 Play」的入口（这是刻意的，见开发计划 R1）
                case SopPhase.Capture:
                    return new string[0];

                case SopPhase.Analyze:
                    return new[]
                    {
                        "get_summary", "get_metrics", "get_frames", "get_markers", "get_findings",
                        "get_asset_issues", "get_scene_issues", "get_code_issues"
                    };

                case SopPhase.Propose:
                    return new[] { "get_fix_plan", "diff_snapshots", "run_rules", "rerun_audit" };

                case SopPhase.AwaitConsent:
                    return new string[0];

                case SopPhase.Execute:
                    // 真正的执行入口在面板（人点按钮）或 MCP 操作通道（带同意凭据）；
                    // Agent 的工具集里刻意不存在写操作。
                    return new string[0];

                case SopPhase.Verify:
                    return new[] { "rerun_audit", "diff_snapshots", "get_metrics", "get_findings" };

                case SopPhase.Deliver:
                    return new[] { "export_report", "get_fix_plan" };

                default:
                    return new string[0];
            }
        }

        /// <summary>某个工具在当前阶段是否允许调用。</summary>
        public static bool Allows(SopPhase phase, string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            var tools = ToolsFor(phase);
            for (int i = 0; i < tools.Length; i++) if (tools[i] == toolName) return true;
            return false;
        }

        public static string PhaseName(SopPhase phase)
        {
            switch (phase)
            {
                case SopPhase.Goal: return "S1 目标确认";
                case SopPhase.Capture: return "S2 采集（由人操作）";
                case SopPhase.Analyze: return "S3 只读分析";
                case SopPhase.Propose: return "S4 提方案";
                case SopPhase.AwaitConsent: return "S5 等人工同意";
                case SopPhase.Execute: return "S6 执行";
                case SopPhase.Verify: return "S7 验证";
                case SopPhase.Deliver: return "S8 交付";
                default: return "未开始";
            }
        }

        /// <summary>把 SOP 渲染成给模型看的文本（注入系统提示词）。</summary>
        public static string PromptSection(SopPhase phase = SopPhase.Analyze)
        {
            var sb = new StringBuilder();
            sb.Append("## 你的操作流程（SOP v").Append(Version).Append("，必须按阶段来）\n");
            sb.Append("当前阶段：**").Append(PhaseName(phase)).Append("**\n\n");

            sb.Append("阶段与允许的工具：\n");
            var phases = (SopPhase[])Enum.GetValues(typeof(SopPhase));
            for (int i = 0; i < phases.Length; i++)
            {
                var p = phases[i];
                if (p == SopPhase.Idle) continue;
                var tools = ToolsFor(p);
                sb.Append("- ").Append(PhaseName(p)).Append("：")
                  .Append(tools.Length == 0 ? "不调用任何工具" : string.Join(" / ", tools))
                  .Append('\n');
            }

            sb.Append("\n硬规则（违反即视为无效回答）：\n");
            for (int i = 0; i < HardRules.Length; i++)
                sb.Append("- ").Append(HardRules[i]).Append('\n');

            sb.Append("\n采集环节只能**引导用户**去做：让他在面板点「跟随采集」—— 工具会量一次基线、"
                    + "自动进入 Play 并开始记录，用户只管玩。你自己没有进 Play 的工具。\n");
            sb.Append("任何需要改工程的动作，必须停在「S5 等人工同意」，"
                    + "把动作清单（目标 / 影响面 / 风险 / 回滚）列清楚后交给用户点确认。\n");

            return sb.ToString();
        }

        /// <summary>面板「SOP」标签里展示的完整流程说明。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("SOP v").Append(Version).Append(" —— AI 的标准操作流程\n\n");
            sb.Append("流程：S1 目标确认 → S2 采集（人操作）→ S3 只读分析 → S4 提方案 → ")
              .Append("S5 等人工同意 → S6 执行 → S7 验证 → S8 交付\n\n");
            sb.Append("每阶段的允许工具：\n");
            var phases = (SopPhase[])Enum.GetValues(typeof(SopPhase));
            for (int i = 0; i < phases.Length; i++)
            {
                var p = phases[i];
                if (p == SopPhase.Idle) continue;
                var tools = ToolsFor(p);
                sb.Append("  ").Append(PhaseName(p)).Append("：")
                  .Append(tools.Length == 0 ? "（不调用工具）" : string.Join(" / ", tools)).Append('\n');
            }
            sb.Append("\n硬规则：\n");
            for (int i = 0; i < HardRules.Length; i++) sb.Append("  ").Append(HardRules[i]).Append('\n');
            sb.Append("\n流程追溯：每次分析写 `Log/index.jsonl`，每次改工程/撤销写 `Log/operations.jsonl`")
              .Append("（含同意来源，可用 IAnalysisStore 换成 SQLite）。\n");
            return sb.ToString();
        }
    }
}

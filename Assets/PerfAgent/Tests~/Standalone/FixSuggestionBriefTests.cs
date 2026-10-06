using System;
using System.Collections.Generic;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「哪些代码需要修、怎么修」的两条产出口径：本地清单（事实）与 AI 底稿（建议的输入）。
    ///
    /// 这里钉的是两件很实际的事：
    ///   1. 本地清单必须能独立使用（按文件分组、带文件:行、并如实说明「不含具体改法」）；
    ///   2. AI 底稿必须**尊重代码外发开关** —— 关着的时候一个字符的源码都不能带上去。
    /// </summary>
    static class FixSuggestionBriefTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(LocalChecklistGroupsByFileAndStaysHonest);
            tests.Add(AiBriefRespectsCodeUploadSwitch);
            tests.Add(AiBriefCarriesFindingsAndForbidsInvention);
        }

        static PerfSnapshot Snapshot()
        {
            var s = new PerfSnapshot { id = "s-fix", scenePath = "Assets/Scenes/Battle.unity" };
            s.codeIssues.Add(new CodeIssue
            {
                file = "Assets/Game/EnemyAI.cs", line = 42, severity = Severity.Warn,
                pattern = "Update + gc_string_concat", suggestion = "用 StringBuilder 或缓存字符串",
                snippet = "label.text = \"hp \" + hp + \"/\" + maxHp;"
            });
            s.codeIssues.Add(new CodeIssue
            {
                file = "Assets/Game/EnemyAI.cs", line = 88, severity = Severity.Warn,
                pattern = "Update + camera_main", suggestion = "缓存 Camera.main",
                snippet = "transform.LookAt(Camera.main.transform);"
            });
            s.codeIssues.Add(new CodeIssue
            {
                file = "Assets/Game/Inventory.cs", line = 12, severity = Severity.Error,
                pattern = "Update + linq", suggestion = "改成 for 循环或缓存结果",
                snippet = "var alive = enemies.Where(e => e.hp > 0).ToList();"
            });

            s.findings.Add(new PerfFinding
            {
                id = "gc_alloc_per_frame", category = "内存", severity = Severity.Error,
                title = "项目每帧托管分配约 13970 B，超出预算 2048 B",
                recommendation = "优先处理每帧 new 容器/字符串/LINQ 的写法。"
            });
            return s;
        }

        /// <summary>本地清单要能一个人独立用：按文件分组（一次打开一个文件改完）、带文件:行，且如实说明不含改法。</summary>
        static void LocalChecklistGroupsByFileAndStaysHonest()
        {
            string text = FixSuggestionBrief.LocalChecklist(Snapshot());

            True(text.IndexOf("未使用 AI", StringComparison.Ordinal) >= 0, "checklist must say it is local");
            True(text.IndexOf("## Assets/Game/EnemyAI.cs（2 处）", StringComparison.Ordinal) >= 0,
                "issues of one file must be grouped together");
            True(text.IndexOf("`Assets/Game/Inventory.cs:12`", StringComparison.Ordinal) >= 0,
                "every item must carry file:line");
            True(text.IndexOf("Update + gc_string_concat", StringComparison.Ordinal) >= 0, "must carry the pattern");
            True(text.IndexOf("不含「改成什么代码」", StringComparison.Ordinal) >= 0,
                "checklist must be honest about not providing the actual fix");
            True(text.IndexOf("相关性能结论", StringComparison.Ordinal) >= 0, "checklist must attach related findings");
        }

        /// <summary>关着「允许上传代码片段」时，底稿里不能出现任何一行源码 —— 隐私开关必须在内容里生效。</summary>
        static void AiBriefRespectsCodeUploadSwitch()
        {
            var s = Snapshot();

            string without = FixSuggestionBrief.Build(s, false);
            True(without.IndexOf("EnemyAI.cs:42", StringComparison.Ordinal) >= 0,
                "file:line must still be sent (that is what makes the advice concrete)");
            True(without.IndexOf("label.text", StringComparison.Ordinal) < 0,
                "source snippets must not be sent when the upload switch is off");
            True(without.IndexOf("Camera.main.transform", StringComparison.Ordinal) < 0,
                "source snippets must not be sent when the upload switch is off");
            True(without.IndexOf("关闭了「允许上传代码片段」", StringComparison.Ordinal) >= 0,
                "the brief must tell the model why snippets are missing");

            string with = FixSuggestionBrief.Build(s, true);
            True(with.IndexOf("label.text", StringComparison.Ordinal) >= 0,
                "snippets must be sent when the user allowed it");
        }

        /// <summary>底稿要带上相关结论，并明确禁止编造位置 —— 不然模型会给出一堆不存在的文件行号。</summary>
        static void AiBriefCarriesFindingsAndForbidsInvention()
        {
            string brief = FixSuggestionBrief.Build(Snapshot(), true);

            True(brief.IndexOf("项目每帧托管分配约 13970 B", StringComparison.Ordinal) >= 0,
                "brief must carry the performance findings the code is suspected of causing");
            True(brief.IndexOf("不要编造清单以外的位置", StringComparison.Ordinal) >= 0,
                "brief must forbid inventing locations");
            True(FixSuggestionBrief.Prompt.IndexOf("不要复述", StringComparison.Ordinal) >= 0,
                "the instruction must forbid repeating the raw list back at the user");
            True(FixSuggestionBrief.Prompt.IndexOf("代码块", StringComparison.Ordinal) >= 0,
                "the instruction must ask for concrete replaceable code");
        }

        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}

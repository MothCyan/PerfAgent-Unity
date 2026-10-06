using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 「哪些代码需要修、怎么修」这份东西的两条产出口径：
    ///
    ///   · <see cref="LocalChecklist"/> —— **本地版**：规则扫描给出的清单
    ///     （文件:行 + 模式 + 规则建议），不联网、随时可复制。
    ///   · <see cref="Build"/> —— **给 AI 的事实底稿**：上面那份清单加上代码片段，
    ///     让 LLM 输出「改成什么代码」这种规则给不了的结论。
    ///
    /// 为什么两者分开：本地清单是事实（可回溯、可核对），AI 版是建议（可能出错、需要人看）。
    /// 混在一起会让人分不清哪句是扫出来的、哪句是模型说的。
    ///
    /// 隐私：<see cref="Build"/> 的 includeSnippets 必须由调用方按用户配置传入 ——
    /// 「允许上传代码片段」关着的时候，只发文件路径与模式名，别偷偷把源码带出去。
    /// </summary>
    public static class FixSuggestionBrief
    {
        /// <summary>给 LLM 的指令（问题侧文本）。要求它给出可执行的改法，并且不要复述原文。</summary>
        public const string Prompt =
            "把下面这些代码问题整理成一份可直接动手的修复清单：\n"
            + "1. 按「影响从大到小」排序，每条写清 文件:行、问题是什么、为什么会影响性能；\n"
            + "2. 给出**具体的改法**，关键改动用代码块写出可以替换的代码（改前 → 改后）；\n"
            + "3. 能用 BCL/Unity 现成 API 解决的，写出 API 名；\n"
            + "4. 不要复述我给你的清单原文，也不要编造我没给你的文件或行号；\n"
            + "5. 如果某条信息不足以给出改法，直接说「需要看上下文」，不要猜。";

        /// <summary>
        /// 本地清单（Markdown）。不含 AI、不含具体改法 —— 就是规则扫描的原文整理，
        /// 顺手按文件分组，方便一个人一次性改完一个文件。
        /// </summary>
        public static string LocalChecklist(PerfSnapshot s, int maxIssues = 200)
        {
            var sb = new StringBuilder();
            sb.Append("# PerfAgent 修复清单（本地规则扫描，未使用 AI）\n\n");

            if (s == null)
            {
                sb.Append("（当前没有快照）\n");
                return sb.ToString();
            }

            sb.Append("- 快照：").Append(string.IsNullOrEmpty(s.id) ? "（未命名）" : s.id).Append('\n');
            if (!string.IsNullOrEmpty(s.scenePath)) sb.Append("- 场景：").Append(s.scenePath).Append('\n');
            sb.Append("- 代码反模式：").Append(s.codeIssues.Count).Append(" 处");
            if (s.codeIssues.Count > maxIssues) sb.Append("（下面只列前 ").Append(maxIssues).Append(" 处）");
            sb.Append("\n\n");

            if (s.codeIssues.Count == 0)
            {
                sb.Append("规则扫描没有发现代码反模式。\n");
                AppendRelatedFindings(sb, s);
                sb.Append("\n> 想看「改成什么代码」，请开启 AI 后点「复制修复建议」。\n");
                return sb.ToString();
            }

            // 按文件分组：同一个人改代码时是一次打开一个文件，不是一个问题一个文件来回跳
            var order = new List<string>();
            var byFile = new Dictionary<string, List<CodeIssue>>();
            for (int i = 0; i < s.codeIssues.Count && i < maxIssues; i++)
            {
                var c = s.codeIssues[i];
                string file = string.IsNullOrEmpty(c.file) ? "(未知文件)" : c.file;
                if (!byFile.ContainsKey(file)) { byFile[file] = new List<CodeIssue>(); order.Add(file); }
                byFile[file].Add(c);
            }

            for (int i = 0; i < order.Count; i++)
            {
                string file = order[i];
                var list = byFile[file];
                sb.Append("## ").Append(file).Append("（").Append(list.Count).Append(" 处）\n\n");
                for (int k = 0; k < list.Count; k++)
                {
                    var c = list[k];
                    sb.Append("- ").Append('`').Append(file).Append(':').Append(c.line.ToString(CultureInfo.InvariantCulture)).Append("` ")
                      .Append('[').Append(Label(c.severity)).Append("] ").Append(c.pattern).Append('\n');
                    if (!string.IsNullOrEmpty(c.suggestion)) sb.Append("  规则建议：").Append(c.suggestion).Append('\n');
                    if (!string.IsNullOrEmpty(c.snippet)) sb.Append("  代码：").Append(OneLine(c.snippet)).Append('\n');
                }
                sb.Append('\n');
            }

            AppendRelatedFindings(sb, s);
            sb.Append("\n> 这是规则扫描给出的清单（文件:行 + 模式 + 规则建议），**不含「改成什么代码」**。\n")
              .Append("> 要具体改法（含可替换的代码），开启 AI 后点「复制修复建议」。\n");
            return sb.ToString();
        }

        /// <summary>
        /// 给 AI 的事实底稿：清单 + （可选）代码片段 + 相关的性能结论。
        /// includeSnippets = false 时不带源码，只给文件:行与模式名（用户关掉了代码外发）。
        /// </summary>
        public static string Build(PerfSnapshot s, bool includeSnippets, int maxIssues = 40)
        {
            var sb = new StringBuilder();
            if (s == null) return "（当前没有快照）";

            sb.Append("快照 ").Append(string.IsNullOrEmpty(s.id) ? "（未命名）" : s.id);
            if (!string.IsNullOrEmpty(s.scenePath)) sb.Append("，场景 ").Append(s.scenePath);
            sb.Append('\n');
            sb.Append("规则扫描到 ").Append(s.codeIssues.Count).Append(" 处代码反模式");
            if (s.codeIssues.Count > maxIssues) sb.Append("（下面列前 ").Append(maxIssues).Append(" 处）");
            sb.Append("：\n");

            if (s.codeIssues.Count == 0)
            {
                sb.Append("- （没有扫到代码反模式）\n");
            }
            else
            {
                for (int i = 0; i < s.codeIssues.Count && i < maxIssues; i++)
                {
                    var c = s.codeIssues[i];
                    sb.Append(i + 1).Append(". ").Append(c.file).Append(':').Append(c.line.ToString(CultureInfo.InvariantCulture))
                      .Append("  [").Append(Label(c.severity)).Append("] ").Append(c.pattern);
                    if (!string.IsNullOrEmpty(c.suggestion)) sb.Append("　规则建议：").Append(c.suggestion);
                    sb.Append('\n');
                    if (includeSnippets && !string.IsNullOrEmpty(c.snippet))
                        sb.Append("   代码：").Append(OneLine(c.snippet)).Append('\n');
                }
            }

            if (!includeSnippets)
            {
                sb.Append("（用户关闭了「允许上传代码片段」，所以上面没有代码正文 —— 只在结论里能给出方向，"
                          + "需要具体代码时请说明「需要看上下文」。）\n");
            }

            AppendRelatedFindings(sb, s, true);
            sb.Append("注意：上面的文件与行号来自规则扫描，可直接引用；不要编造清单以外的位置。\n");
            return sb.ToString();
        }

        static void AppendRelatedFindings(StringBuilder sb, PerfSnapshot s, bool forPrompt = false)
        {
            if (s == null || s.findings == null || s.findings.Count == 0) return;

            var sb2 = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < s.findings.Count && shown < 8; i++)
            {
                var f = s.findings[i];
                if (f.severity == Severity.Info) continue;
                sb2.Append("- [").Append(Label(f.severity)).Append("] ").Append(f.title);
                if (!string.IsNullOrEmpty(f.recommendation)) sb2.Append("　建议：").Append(f.recommendation);
                sb2.Append('\n');
                shown++;
            }
            if (shown == 0) return;

            sb.Append(forPrompt ? "\n相关的性能结论（可能与这些代码有关）：\n" : "\n## 相关性能结论\n\n");
            sb.Append(sb2);
        }

        static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.IndexOf("  ", StringComparison.Ordinal) >= 0) t = t.Replace("  ", " ");
            return t.Length > 200 ? t.Substring(0, 200) + "…" : t;
        }

        static string Label(string severity)
        {
            if (severity == Severity.Error) return "严重";
            if (severity == Severity.Warn) return "警告";
            return "提示";
        }
    }
}

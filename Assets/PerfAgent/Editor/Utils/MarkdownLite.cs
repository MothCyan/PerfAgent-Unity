using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 把 Markdown 收敛成 UI Toolkit 的 Label 认得的几个标签。
    ///
    /// <para><b>为什么需要它</b></para>
    /// 对话记录、结论卡片都用 Label 显示，Label 只认
    /// <c>&lt;b&gt; / &lt;i&gt; / &lt;color&gt;</c>。而模型（以及我们自己的报告片段）写的是 Markdown，
    /// 于是 <c>### 标题</c>、<c>---</c>、<c>| 表 | 格 |</c>、三反引号代码围栏都会**原样**显示出来，
    /// 一屏符号 —— 用户反馈的「不好读」多半就是它。
    ///
    /// <para><b>它不做什么</b></para>
    /// 不做 Markdown 渲染，只做**看得懂的降级**：
    ///   标题 → 粗体行；水平线 → 一行淡色横线；表格 → 「列 · 列」；代码围栏 → 删掉围栏本身。
    /// 渲染层能给的远比这多，但 Label 给不了；多写一套解析器不如把格式收敛掉。
    /// </summary>
    public static class MarkdownLite
    {
        /// <summary>水平线的颜色（比正文淡，避免抢注意力）。</summary>
        const string RuleColor = "#3A3F47";
        const string CodeColor = "#9CDCFE";

        static readonly Regex HeadingRegex = new Regex(@"^[ \t]*#{1,6}[ \t]+(.*)$", RegexOptions.Compiled);
        static readonly Regex RuleRegex = new Regex(@"^[ \t]*([-*_])[ \t]*\1[ \t]*\1[-*_ \t]*$", RegexOptions.Compiled);
        static readonly Regex FenceRegex = new Regex("^[ \t]*(```|~~~)", RegexOptions.Compiled);
        static readonly Regex TableSeparatorRegex = new Regex(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);

        /// <summary>Markdown → Label 富文本。输入里的 `&lt;` 会被转义，不会变成标签。</summary>
        public static string ToRichText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            var sb = new StringBuilder(markdown.Length + 32);
            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                string escaped = Escape(line);

                if (FenceRegex.IsMatch(line))
                {
                    inCode = !inCode;   // 围栏本身不显示：Label 里显示三个反引号只是噪声
                    continue;
                }

                if (inCode)
                {
                    sb.Append("  ").Append(escaped);   // 代码块靠缩进区分
                    sb.Append('\n');
                    continue;
                }

                var heading = HeadingRegex.Match(line);
                if (heading.Success)
                {
                    sb.Append("\n<b>").Append(Escape(heading.Groups[1].Value.Trim())).Append("</b>\n");
                    continue;
                }

                if (RuleRegex.IsMatch(line))
                {
                    sb.Append("<color=").Append(RuleColor).Append('>')
                      .Append(new string('─', 28))
                      .Append("</color>\n");
                    continue;
                }

                // 表格：丢掉分隔行，单元格之间用中点连接 —— 比一排竖线好读
                if (line.TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    if (TableSeparatorRegex.IsMatch(line)) continue;
                    sb.Append(TableRow(line));
                    sb.Append('\n');
                    continue;
                }

                sb.Append(escaped).Append('\n');
            }

            string text = sb.ToString();
            // 行内：先粗体、后代码。都在转义之后做，所以内容里的 < > 不会被当成标签。
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
            text = Regex.Replace(text, @"`([^`]+)`", "<color=" + CodeColor + ">$1</color>");
            return text;
        }

        static string TableRow(string line)
        {
            var cells = new List<string>();
            string[] parts = line.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string cell = parts[i].Trim();
                if (cell.Length == 0) continue;     // 首尾的空段（行首/行尾的竖线）
                cells.Add(cell);
            }
            if (cells.Count == 0) return "";

            var sb = new StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append("　·　");
                sb.Append(cells[i]);
            }
            return sb.ToString();
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}

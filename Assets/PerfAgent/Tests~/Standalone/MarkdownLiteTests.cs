using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「Markdown 降级成 Label 富文本」的回归。
    ///
    /// 背景：对话记录是 Label，只认 `&lt;b&gt; / &lt;i&gt; / &lt;color&gt;`，而模型写的是 Markdown。
    /// 于是 `### 标题`、`---`、`| 表 | 格 |`、三反引号围栏会原样显示 —— 用户看到的就是一屏符号
    /// （实测反馈「不好读」）。这批用例守住：这些结构必须被**降级**，而不是漏过去。
    /// </summary>
    static class MarkdownLiteTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(MarkdownLiteDowngradesStructure);
            tests.Add(MarkdownLiteKeepsCodeAndInlineFormatting);
            tests.Add(MarkdownLiteEscapesAngleBrackets);
        }

        /// <summary>标题、水平线、表格都必须被降级，一个原始符号都不该留在输出里。</summary>
        static void MarkdownLiteDowngradesStructure()
        {
            string md = "### 1. 每帧分配超标 7.05 倍\n\n"
                      + "| 指标 | 值 | 预算 |\n|---|---:|---:|\n"
                      + "| 每帧托管分配 | 14,443.6 B | 2,048 B |\n\n"
                      + "---\n\n"
                      + "正文一句。\n";

            string rich = MarkdownLite.ToRichText(md);

            True(rich.IndexOf("###", StringComparison.Ordinal) < 0, "raw headings must not survive: " + rich);
            True(rich.IndexOf("<b>1. 每帧分配超标 7.05 倍</b>", StringComparison.Ordinal) >= 0,
                "heading must become a bold line: " + rich);
            True(rich.IndexOf("|---", StringComparison.Ordinal) < 0, "table separator must be dropped");
            True(rich.IndexOf("| 每帧托管分配 |", StringComparison.Ordinal) < 0, "table rows must not stay as pipes");
            True(rich.IndexOf("每帧托管分配　·　14,443.6 B　·　2,048 B", StringComparison.Ordinal) >= 0,
                "table cells must be joined readably: " + rich);
            True(rich.IndexOf("\n---", StringComparison.Ordinal) < 0, "horizontal rule must not stay as ---");
            True(rich.IndexOf("────", StringComparison.Ordinal) >= 0, "horizontal rule must become a dim line");
            True(rich.IndexOf("正文一句。", StringComparison.Ordinal) >= 0, "plain text must survive");
        }

        /// <summary>代码围栏本身删掉、代码内容保留；粗体与行内代码继续生效。</summary>
        static void MarkdownLiteKeepsCodeAndInlineFormatting()
        {
            string md = "改成：\n```csharp\nfor (int i = 0; i < n; i++) { }\n```\n"
                      + "注意 **别用 LINQ**，用 `for` 循环。\n";

            string rich = MarkdownLite.ToRichText(md);

            True(rich.IndexOf("```", StringComparison.Ordinal) < 0, "fence markers must be removed: " + rich);
            // 代码内容保留，但尖括号按规则转义（Label 只认标签，不认源码里的 <）
            True(rich.IndexOf("for (int i = 0; i &lt; n; i++) { }", StringComparison.Ordinal) >= 0,
                "code content must survive (escaped): " + rich);
            True(rich.IndexOf("<b>别用 LINQ</b>", StringComparison.Ordinal) >= 0, "bold must work");
            True(rich.IndexOf("<color=", StringComparison.Ordinal) >= 0, "inline code must be colored");
        }

        /// <summary>内容里的尖括号必须被转义 —— 否则用户/模型的文本能注入标签。</summary>
        static void MarkdownLiteEscapesAngleBrackets()
        {
            string rich = MarkdownLite.ToRichText("分配在 <Update> 里，见 List<int>。");

            True(rich.IndexOf("&lt;Update&gt;", StringComparison.Ordinal) >= 0, "angle brackets must be escaped: " + rich);
            True(rich.IndexOf("List&lt;int&gt;", StringComparison.Ordinal) >= 0, "generics must be escaped: " + rich);
            True(rich.IndexOf("<Update>", StringComparison.Ordinal) < 0, "raw tag must not survive");
        }

        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}

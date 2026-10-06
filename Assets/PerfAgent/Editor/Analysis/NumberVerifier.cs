using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 幻觉校验的数值口径：判断 LLM 回答里出现的数字能不能**回溯**到证据。
    ///
    /// <para><b>它要挡的是什么</b></para>
    /// 模型自己编出来的数字（尤其是「某个文件每帧分配 12.3 KB」这种具体到可疑的）。
    ///
    /// <para><b>它不能挡什么（否则就是误报）</b></para>
    /// 下面这几类都是**可回溯**的，早期实现把它们当可疑值列进附录，结果附录里一半是误报
    /// （用户反馈过「这堆是什么」）：
    ///   · 千位分隔符：`658,534,588` 被正则拆成 658 / 534 / 588 三个数字（3 条误报）；
    ///   · 精度不同：我们显示 3.55，模型写 3.5469（同一个数）；
    ///   · 单位换算：578323712 B → 551.4 MiB；
    ///   · 由证据推出来的：倍数（14443.6 / 2048 ≈ 7）、差值（578323712 − 536870912 = 41452800 → 41.45 MB）、百分比。
    ///
    /// 所以现在的判定是「能不能由证据经**四则运算 + 换单位**得到」，而不是「字符串是否出现过」。
    /// </summary>
    public static class NumberVerifier
    {
        /// <summary>
        /// 取自文本的数字。
        ///
        /// 两个分支：带千位分隔符的写法（1,234,567 / 1，234 / 1_234）优先，
        /// 其次是普通数字。前后的 `(?&lt;![\w.])` / `(?![\w])` 是为了不把
        /// 标识符里的数字（Assets/Foo2.cs 的 2）当成读数。
        /// </summary>
        static readonly Regex NumberRegex = new Regex(
            @"(?<![\w.])(?:[0-9]{1,3}(?:[,，_][0-9]{3})+|[0-9]+)(?:\.[0-9]+)?(?![\w])",
            RegexOptions.Compiled);

        /// <summary>这些单独出现时不值得对账（预算里到处是 0/1，帧号里到处是 2/3）。</summary>
        static readonly HashSet<string> Ignorable = new HashSet<string> { "0", "1", "2", "3" };

        /// <summary>相对容差：显示层会四舍五入（3.5469 ↔ 3.55），0.5% 足够盖住，又不足以放过编造值。</summary>
        const double RelativeTolerance = 0.005;
        const double AbsoluteTolerance = 0.5;

        /// <summary>几十条换算候选 × 上百个证据值，数量级很小，直接算。用于单位换算。</summary>
        static readonly double[] Scales = { 1000.0, 1024.0, 1e6, 1048576.0, 1e9, 1073741824.0 };
        static readonly double[] Inverses = { 0.001, 1.0 / 1024.0, 1e-6, 1.0 / 1048576.0, 1e-9, 1.0 / 1073741824.0 };

        /// <summary>
        /// 找出 <paramref name="text"/> 中无法回溯的数值（人类可读的「值 ← 上下文」列表）。
        /// </summary>
        /// <param name="dataText">
        /// 报告里由采集器写入的数据部分（环境表、指标表、口径说明等）。
        /// 这些都是从快照直接抄的，不需要对账 —— 传进来可避免把「2026（年份）」
        /// 「5060（显卡型号）」这类事实当成可疑数字。
        /// </param>
        public static List<string> Unverified(string text, PerfSnapshot s, string dataText = null)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            var allowedText = new HashSet<string>();
            var allowedNumbers = new List<double>();
            CollectAllowed(s, dataText, allowedText, allowedNumbers);

            var seen = new HashSet<string>();
            foreach (Match m in NumberRegex.Matches(text))
            {
                string token = m.Value;
                string normalized = Normalize(token);

                if (Ignorable.Contains(normalized)) continue;
                if (allowedText.Contains(normalized)) continue;
                if (IsTraceable(normalized, allowedNumbers)) continue;
                if (seen.Contains(normalized)) continue;
                seen.Add(normalized);

                int start = Math.Max(0, m.Index - 24);
                int len = Math.Min(text.Length - start, 56);
                result.Add(token + "  ←  ..." + text.Substring(start, len).Replace("\n", " ").Trim() + "...");
            }
            return result;
        }

        /// <summary>把快照（+ 可选数据文本）里所有能当「出处」的数值收集起来。</summary>
        public static void CollectAllowed(PerfSnapshot s, string dataText,
            HashSet<string> allowedText, List<double> allowedNumbers)
        {
            if (s != null)
            {
                for (int i = 0; i < s.metrics.Count; i++)
                {
                    var m = s.metrics[i];
                    AddToken(allowedText, allowedNumbers, Fmt(m.value));
                    AddToken(allowedText, allowedNumbers, m.value.ToString("0.##########", CultureInfo.InvariantCulture));
                    AddToken(allowedText, allowedNumbers, m.budget);
                    // min / max / 样本数也是工具给出的读数，模型可能会引用
                    if (m.samples > 0) AddToken(allowedText, allowedNumbers, m.samples.ToString(CultureInfo.InvariantCulture));
                }

                for (int i = 0; i < s.findings.Count; i++)
                {
                    var f = s.findings[i];
                    for (int k = 0; k < f.evidence.Count; k++)
                    {
                        var e = f.evidence[k];
                        AddToken(allowedText, allowedNumbers, e.value);
                        AddToken(allowedText, allowedNumbers, e.threshold);
                    }
                }

                if (s.capturedFrameCount > 0) AddToken(allowedText, allowedNumbers, s.capturedFrameCount.ToString(CultureInfo.InvariantCulture));
                if (s.targetFrameRate > 0) AddToken(allowedText, allowedNumbers, s.targetFrameRate.ToString(CultureInfo.InvariantCulture));
                if (s.physicsSolverIterations > 0) AddToken(allowedText, allowedNumbers, s.physicsSolverIterations.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < s.notes.Count; i++) AddToken(allowedText, allowedNumbers, s.notes[i]);
                for (int i = 0; i < s.codeIssues.Count; i++)
                {
                    AddToken(allowedText, allowedNumbers, s.codeIssues[i].line.ToString(CultureInfo.InvariantCulture));
                }
            }

            // 采集器写进报告的数据表：整体扫一遍
            AddToken(allowedText, allowedNumbers, dataText);
        }

        /// <summary>把一段文本里出现的数字全部登记为「有出处」。</summary>
        static void AddToken(HashSet<string> allowedText, List<double> allowedNumbers, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;

            foreach (Match m in NumberRegex.Matches(raw))
            {
                string normalized = Normalize(m.Value);
                allowedText.Add(normalized);

                double d;
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    allowedNumbers.Add(d);
                    // 显示层会四舍五入：3.5469 与 3.55 要能互认
                    allowedText.Add(d.ToString("0.##", CultureInfo.InvariantCulture));
                    allowedText.Add(d.ToString("0.#", CultureInfo.InvariantCulture));
                    allowedText.Add(d.ToString("0", CultureInfo.InvariantCulture));
                    allowedText.Add(Math.Round(d).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>去掉千位分隔符：658,534,588 → 658534588。</summary>
        public static string Normalize(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            return token.Replace(",", "").Replace("，", "").Replace("_", "").Trim();
        }

        /// <summary>
        /// 这个数能不能由证据「算」出来：完全相同、仅差精度、换单位、求和差、求倍数、百分比。
        ///
        /// 注意容差是小尺度相对值（0.5%），不是「大概像就算」——
        /// 编造的数字（比如凭空写个 12345 B/帧）依然会被列出来。
        /// </summary>
        static bool IsTraceable(string normalized, List<double> allowed)
        {
            double v;
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;

            for (int i = 0; i < allowed.Count; i++)
            {
                double a = allowed[i];
                if (Near(v, a)) return true;

                // 单位换算：字节 ↔ KB/MB/GB（1000 与 1024 两套都认）
                for (int k = 0; k < Scales.Length; k++)
                {
                    if (Near(v, a / Scales[k])) return true;
                    if (Near(v, a * Scales[k])) return true;
                    if (Near(v, a * Inverses[k])) return true;
                }

                // 百分比
                if (Near(v, a * 100.0)) return true;

                // 两两组合：倍数、差值、和
                for (int j = 0; j < allowed.Count; j++)
                {
                    double b = allowed[j];
                    if (b == 0) continue;

                    if (Near(v, a / b)) return true;
                    if (Near(v, a - b)) return true;
                    if (Near(v, a + b)) return true;

                    // 差值的单位换算（例：内存差 41452800 B → 41.45 MB）
                    double diff = a - b;
                    for (int k = 0; k < Scales.Length; k++)
                        if (Near(v, diff / Scales[k])) return true;
                }
            }
            return false;
        }

        static bool Near(double v, double a)
        {
            if (double.IsNaN(a) || double.IsInfinity(a)) return false;
            double diff = Math.Abs(v - a);
            if (diff <= AbsoluteTolerance) return true;
            double scale = Math.Max(Math.Abs(a), Math.Abs(v));
            return scale > 0 && diff / scale <= RelativeTolerance;
        }

        /// <summary>与报告导出一致的数字格式（两/三位小数、整数不带小数点）。</summary>
        static string Fmt(double value)
        {
            if (double.IsNaN(value)) return "-";
            if (Math.Abs(value) >= 1000) return value.ToString("0", CultureInfo.InvariantCulture);
            if (Math.Abs(value) >= 10) return value.ToString("0.#", CultureInfo.InvariantCulture);
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}

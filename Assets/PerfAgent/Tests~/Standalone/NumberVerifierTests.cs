using System;
using System.Collections.Generic;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「证据校验」的口径回归。
    ///
    /// 这条防线的作用是抓编造的数字，但早期实现只做字符串比对，误报一半：
    ///   · `658,534,588` 被拆成 658 / 534 / 588（千位分隔符）；
    ///   · 我们显示 3.55、模型写 3.5469（同一数值，精度不同）→ 被当成没出处；
    ///   · `578323712 B → 551.4 MiB`、`14443.6 / 2048 ≈ 7`（单位换算与倍数）→ 被当成没出处。
    /// 附录里塞一堆我们的自己的数字，用户只会当它是噪声（实测反馈）。
    /// 这批用例守住两件事：**可回溯的必须放行，编造的必须揪出来**。
    /// </summary>
    static class NumberVerifierTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(VerifierAcceptsRestatedAndRoundedValues);
            tests.Add(VerifierAcceptsConversionsAndArithmetic);
            tests.Add(VerifierCatchesFabricatedNumbers);
            tests.Add(VerifierIgnoresLowercaseIdentifiersAndSmallInts);
        }

        static PerfSnapshot Snapshot()
        {
            var s = new PerfSnapshot { id = "s1", capturedFrameCount = 812 };
            s.SetMetric("每帧托管分配", "B", 14443.6, "面板序列");
            s.SetMetric("帧耗时均值", "ms", 3.5469, "面板抽样");
            s.SetMetric("总分配内存", "B", 658534588.0, "Profiler.GetTotalAllocatedMemoryLong");
            s.SetMetric("纹理内存", "B", 578323712.0, "ProfilerRecorder:Texture Memory");
            // 预算也要登记：模型常写「比预算多 39.5 MiB」这种差值，它必须能被算出来
            var textureBudget = s.SetMetric("纹理内存(估算)", "B", 4194304.0, "按导入设置估算");
            textureBudget.budget = "536870912";

            var f = new PerfFinding { id = "gc_alloc_per_frame", category = "内存", severity = Severity.Error, title = "每帧分配超预算" };
            f.evidence.Add(new PerfEvidence("frame_capture", "项目每帧分配", "13978.6", "B", "2048", "估算"));
            s.findings.Add(f);
            return s;
        }

        /// <summary>同一个数的不同写法（千位分隔符、精度）都必须放行。</summary>
        static void VerifierAcceptsRestatedAndRoundedValues()
        {
            var s = Snapshot();

            var none = NumberVerifier.Unverified(
                "总分配内存 658,534,588 B；帧耗时均值 3.5469 ms；纹理内存 578,323,712 B", s);
            Equal(0, none.Count, "restated and rounded values must not be flagged: " + Join(none));

            // 我们自己的显示格式（3.55）当然也要放行
            var rounded = NumberVerifier.Unverified("帧耗时均值 3.55 ms，窗口 812 帧", s);
            Equal(0, rounded.Count, "display-rounded values must not be flagged: " + Join(rounded));
        }

        /// <summary>单位换算、倍数、差值、百分比都是可回溯的推算，一律放行。</summary>
        static void VerifierAcceptsConversionsAndArithmetic()
        {
            var s = Snapshot();

            // 551.4 MiB / 628.0 MiB 是字节换 MiB；7 是 14443.6 / 2048 的倍数；
            // 39.5 是 578323712 − 536870912 之后换 MiB
            var flagged = NumberVerifier.Unverified(
                "纹理内存 551.4 MiB；总分配内存 628 MiB；超出预算约 7 倍；比预算多 39.5 MiB", s);

            Equal(0, flagged.Count, "unit conversions / ratios must not be flagged: " + Join(flagged));
        }

        /// <summary>真编造的数字必须被揪出来 —— 否则这条防线就是摆设。</summary>
        static void VerifierCatchesFabricatedNumbers()
        {
            var s = Snapshot();

            var flagged = NumberVerifier.Unverified(
                "Assets/Game/EnemyAI.cs:42 每帧分配 12345678 B，占了 99.9% 的时间", s);

            True(flagged.Count >= 1, "a fabricated number must be reported");
            True(Join(flagged).IndexOf("12345678", StringComparison.Ordinal) >= 0,
                "the fabricated value must appear in the list: " + Join(flagged));
        }

        /// <summary>标识符里的数字与 0/1/2/3 这类小整数不值得对账（否则附录全是噪声）。</summary>
        static void VerifierIgnoresLowercaseIdentifiersAndSmallInts()
        {
            var s = Snapshot();

            var flagged = NumberVerifier.Unverified("看 Assets/Foo2.cs 与 2 个对象，0 次 GC", s);
            Equal(0, flagged.Count, "identifiers and tiny ints must not be flagged: " + Join(flagged));
        }

        static string Join(List<string> items) { return string.Join(" | ", items.ToArray()); }
        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Equal(int expected, int actual, string label)
        {
            if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}

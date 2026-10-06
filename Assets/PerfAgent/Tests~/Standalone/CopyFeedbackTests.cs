using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「复制」对用户说的话。
    ///
    /// 要挡的事故（实测反馈）：「我现在复制结论，结论不会被复制」——
    /// 复制是静默操作，内容为空 / 生成报告抛异常 / 剪贴板被别的程序占着时，
    /// 用户看到的都是「什么都没发生」，只会以为按钮坏了。这几条钉住：
    /// 每一次复制都必须有一句人看得懂的话，且不能把「回读不一致」说成「已确认成功」。
    /// </summary>
    static class CopyFeedbackTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(EmptyCopySaysWhyNotSilentlyNothing);
            tests.Add(VerifiedCopyReportsCharCount);
            tests.Add(UnverifiedCopyTellsUserToClickAgain);
        }

        static void EmptyCopySaysWhyNotSilentlyNothing()
        {
            string s = CopyFeedback.Describe("Markdown 报告", 0, false);
            True(s.IndexOf("没有可复制的内容", StringComparison.Ordinal) >= 0, "空内容必须明说，不能只说「已复制」：" + s);
            True(s.IndexOf("Markdown 报告", StringComparison.Ordinal) >= 0, "要说清是哪个复制动作：" + s);
            True(s.IndexOf("Console", StringComparison.Ordinal) >= 0, "要给下一步（原因在 Console）：" + s);
        }

        static void VerifiedCopyReportsCharCount()
        {
            True(CopyFeedback.Verified("hello", "hello"), "字符数一致就算写进去了");
            True(!CopyFeedback.Verified("hello", ""), "读回来是空 = 没写进去");
            True(!CopyFeedback.Verified("hello", null), "读不回来 = 不能算成功");
            True(!CopyFeedback.Verified("", ""), "空内容永远不算成功（没东西可复制）");
            True(!CopyFeedback.Verified("hello", "hello world"), "字符数不一致不能算通过");

            string ok = CopyFeedback.Describe("对话记录", 1234, true);
            True(ok.IndexOf("1234", StringComparison.Ordinal) >= 0, "成功要报字符数：" + ok);
            True(ok.IndexOf("回读校验通过", StringComparison.Ordinal) >= 0, ok);
        }

        static void UnverifiedCopyTellsUserToClickAgain()
        {
            string bad = CopyFeedback.Describe("AI 修复清单", 900, false);
            True(bad.IndexOf("不一致", StringComparison.Ordinal) >= 0, "回读不一致要如实说：" + bad);
            // 不能断言「失败」——别的程序可能占着剪贴板，措辞要给出可执行的下一步
            True(bad.IndexOf("再点一次", StringComparison.Ordinal) >= 0, "要给用户可执行的下一步：" + bad);
            True(bad.IndexOf("已复制 ", StringComparison.Ordinal) < 0, "回读不一致时不能说「已复制」：" + bad);
        }

        static void True(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
    }
}

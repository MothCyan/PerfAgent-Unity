using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「点采集后自动进 Play」的判定。
    ///
    /// 这个动作是**替用户按 Play**，判错的代价比一般功能大：已经在 Play 里再按一次会打断当前运行，
    /// 用户刚取消却又被拉进 Play 则是「工具擅自控制编辑器」。所以几种情形逐条钉住。
    /// </summary>
    static class AutoPlayGateTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(AutoPlayGateEnterPlayOnlyWhenItShould);
        }

        static void AutoPlayGateEnterPlayOnlyWhenItShould()
        {
            True(AutoPlayGate.ShouldEnterPlay(true, true, false, false),
                "待命中 + 设置开着 + 不在 Play → 应当自动进 Play");
            True(!AutoPlayGate.ShouldEnterPlay(true, false, false, false),
                "设置里关掉了就不能替用户按 Play");
            True(!AutoPlayGate.ShouldEnterPlay(false, true, false, false),
                "已被取消（不再待命）时不能进 Play");
            True(!AutoPlayGate.ShouldEnterPlay(true, true, true, false),
                "已经在 Play 里不能重复触发");
            True(!AutoPlayGate.ShouldEnterPlay(true, true, false, true),
                "正在切进/切出 Play 时不能插手");
            True(!AutoPlayGate.ShouldEnterPlay(false, false, true, true), "全都不满足时当然不动手");
        }

        static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}

using System;
using System.Collections.Generic;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 采集窗口解析的回归。
    ///
    /// 背景（实测故障）：窗口起点是「开始采集那一刻的面板帧号」，而上一轮采集结束会把 Profiler 关掉
    /// 并清空帧数据 —— 下一轮刚打开记录时 `lastFrameIndex` 还是 -1，起点就永远停在 -1，
    /// 几十秒的 Play 数据在收尾时被判成「采集还没开始就结束了」，报告里连逐帧明细都是 0 帧。
    /// 这批用例钉住：起点缺失时必须回退到「面板里最早的一帧」，并且要标明这是回推的。
    /// </summary>
    static class CaptureWindowTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(CaptureWindowUsesFramesAfterStart);
            tests.Add(CaptureWindowFallsBackWhenStartWasMissing);
            tests.Add(CaptureWindowRejectsEmptyPanel);
            tests.Add(CaptureWindowRejectsWhenNoNewFrames);
            tests.Add(SessionRestartRebasesTheStartFrame);
            tests.Add(LiveStatsRebaseOnFrameIndexRestart);
        }

        /// <summary>
        /// 实测故障：面板帧号是**按 Profiling 会话**递增的，进/退 Play 或清空帧数据后会从头开始。
        /// 而采集起点是「开始那一刻读到的帧号」，可能来自上一个会话（一个很大的旧号）——
        /// 于是 last - startFrame 恒为负，界面上**一直显示「已记录 0 帧」**，玩多久都不动，
        /// 收尾时还会被判成「采集期间没有新帧」。
        /// </summary>
        static void SessionRestartRebasesTheStartFrame()
        {
            // 旧会话留下的大帧号，新会话已从头开始（面板里 0~40 帧）
            True(CaptureWindow.RebaseOnSessionRestart(89188, 0, 40) == 0,
                "帧号往回跳时必须重定到面板最早的一帧");
            // 以下都是「不该动」的情形
            True(CaptureWindow.RebaseOnSessionRestart(5000, 5001, 9000) == 5000, "正常递增时不要动起点");
            True(CaptureWindow.RebaseOnSessionRestart(100, 101, 160) == 100, "正常递增时不要动起点");
            True(CaptureWindow.RebaseOnSessionRestart(-1, 0, 40) == -1, "还没起点时不归它管");
            True(CaptureWindow.RebaseOnSessionRestart(89188, -1, -1) == 89188, "面板还没有帧时不能重定");
            // 面板有帧但读不到 first（极端）—— 退回用 last，至少不要继续算 0
            True(CaptureWindow.RebaseOnSessionRestart(89188, -1, 40) == 40, "拿不到首帧就用最新帧当起点");
        }

        /// <summary>
        /// 实时曲线也吃同一个亏：帧号往回跳时 StartFrame 得重定，否则「已记录 N 帧」恒为 0。
        /// （CaptureLiveStats 不含 UnityEngine 依赖，所以能离线跑）
        /// </summary>
        static void LiveStatsRebaseOnFrameIndexRestart()
        {
            var live = new CaptureLiveStats();
            live.Begin(89188, 0.0);
            True(live.Sample(89189, 0.25), "正常递增时应当产出一个样本");

            // 会话重启：帧号跳回 0
            live.Sample(0, 0.50);
            True(live.PanelFrames == 0, "重启后帧数不能算成负数（界面会显示 0）");

            live.Sample(3, 0.75);
            True(live.PanelFrames == 3, "重启后要从新起点重新起算，实际 " + live.PanelFrames);
        }

        /// <summary>正常情况：只取「起点之后」的帧，不把采集前的帧算进来。</summary>
        static void CaptureWindowUsesFramesAfterStart()
        {
            int first, last;
            bool inferred;
            True(CaptureWindow.TryResolve(100, 90, 160, out first, out last, out inferred), "应当解析出窗口");
            True(first == 101, "窗口起点必须是开始采集之后的第一帧，实际 " + first);
            True(last == 160, "窗口终点取面板最新帧，实际 " + last);
            False(inferred, "有起点时不该标成回推");
        }

        /// <summary>起点缺失（开始时面板还没有帧）→ 回退到面板最早的一帧，并标记为回推。</summary>
        static void CaptureWindowFallsBackWhenStartWasMissing()
        {
            int first, last;
            bool inferred;
            True(CaptureWindow.TryResolve(-1, 200, 260, out first, out last, out inferred), "必须给出兜底窗口");
            True(first == 200, "兜底起点取面板最早的一帧，实际 " + first);
            True(last == 260, "终点取面板最新帧，实际 " + last);
            True(inferred, "回推起点必须被标出来（快照里要写明可能包含采集前后的少量帧）");
        }

        static void CaptureWindowRejectsEmptyPanel()
        {
            int first, last;
            bool inferred;
            False(CaptureWindow.TryResolve(-1, -1, -1, out first, out last, out inferred),
                "面板一帧都没有时必须返回 false，由调用方给出可定位的原因");
            False(CaptureWindow.TryResolve(50, -1, -1, out first, out last, out inferred),
                "lastFrameIndex 为 -1 同样不可用");
        }

        static void CaptureWindowRejectsWhenNoNewFrames()
        {
            int first, last;
            bool inferred;
            False(CaptureWindow.TryResolve(300, 300, 300, out first, out last, out inferred),
                "采集期间没有新帧 → 没有可分析窗口");
            True(CaptureWindow.TryResolve(300, 300, 301, out first, out last, out inferred),
                "哪怕只多一帧也算有窗口");
            True(first == 301, "窗口起点应为 301，实际 " + first);
        }

        static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void False(bool condition, string message)
        {
            if (condition) throw new Exception(message);
        }
    }
}

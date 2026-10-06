using System;
using System.Collections.Generic;
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

using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「采集在跑、Profiler 面板却一帧都不写」时的记录目标试探阶梯。
    ///
    /// 要挡的事故：2026-10-07 现场 `enabled=True，profileEditor=False，first/last=-1`，
    /// 靠猜写了个「Play 下必须 profileEditor=true」，既没有证据、又可能把本来能用的路径弄坏。
    /// 正确姿势是**按顺序实测三种组合**，让「哪一步开始出帧」说话；这里覆盖判定本身。
    /// </summary>
    internal static class RecordingTargetLadderTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(FirstStepDoesNotTouchTheUserTarget);
            tests.Add(StepsCoverBothTargetValuesThenGiveUp);
            tests.Add(NoFramesWithinTheStepKeepsWaiting);
            tests.Add(FramesInAnyStepAcceptImmediately);
        }

        static void FirstStepDoesNotTouchTheUserTarget()
        {
            // 第 0 步 = 不动用户原来的目标：绝大多数情况本来就能录，
            // 一上来就动开关是「猜」，猜错就会把用户能用的路径弄坏。
            True(!RecordingTargetLadder.ProfileEditorFor(0).HasValue, "第 0 步不能改用户的目标");
            True(RecordingTargetLadder.Describe(0).IndexOf("保持", StringComparison.Ordinal) >= 0,
                RecordingTargetLadder.Describe(0));

            // 时间没到就继续等，不能一步都不停地连试三种
            Equal(RecordingTargetLadder.Action.Wait,
                RecordingTargetLadder.Decide(0, RecordingTargetLadder.StepSeconds - 0.1, false), "还没到点要等");
            Equal(RecordingTargetLadder.Action.Advance,
                RecordingTargetLadder.Decide(0, RecordingTargetLadder.StepSeconds, false), "到点无帧就试下一步");
        }

        static void StepsCoverBothTargetValuesThenGiveUp()
        {
            True(RecordingTargetLadder.ProfileEditorFor(1).Value, "第 1 步 = 切「编辑器」目标");
            True(!RecordingTargetLadder.ProfileEditorFor(2).Value, "第 2 步 = 切回「Play Mode / 设备」目标");

            // 两步都要试，试完必须给出「不是目标的问题」的结论，而不是沉默
            Equal(RecordingTargetLadder.Action.Advance,
                RecordingTargetLadder.Decide(1, RecordingTargetLadder.StepSeconds, false), "第 1 步没用要接着试");
            Equal(RecordingTargetLadder.Action.GiveUp,
                RecordingTargetLadder.Decide(2, RecordingTargetLadder.StepSeconds, false), "最后一步没用要收手");
            True(RecordingTargetLadder.AllStepsFailed.IndexOf("内存", StringComparison.Ordinal) >= 0,
                "全失败时必须把注意力引向环境（内存/暂停/编译），而不是让用户继续折腾目标：" + RecordingTargetLadder.AllStepsFailed);
        }

        static void NoFramesWithinTheStepKeepsWaiting()
        {
            for (int step = 0; step < RecordingTargetLadder.StepCount; step++)
            {
                Equal(RecordingTargetLadder.Action.Wait,
                    RecordingTargetLadder.Decide(step, 0.0, false), "刚换到第 " + step + " 步时要先等");
            }
        }

        static void FramesInAnyStepAcceptImmediately()
        {
            for (int step = 0; step < RecordingTargetLadder.StepCount; step++)
            {
                Equal(RecordingTargetLadder.Action.Accept,
                    RecordingTargetLadder.Decide(step, 0.0, true), "第 " + step + " 步一出帧就该停（不用等满）");
                Equal(RecordingTargetLadder.Action.Accept,
                    RecordingTargetLadder.Decide(step, RecordingTargetLadder.StepSeconds * 2, true),
                    "第 " + step + " 步超时后出帧也要接受");
            }
        }

        // 本地断言（与其它测试文件一致：每个文件自带，不依赖别人的 helper）
        static void True(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        static void Equal<T>(T expected, T actual, string label)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(label + ": expected " + expected + ", got " + actual);
        }
    }
}

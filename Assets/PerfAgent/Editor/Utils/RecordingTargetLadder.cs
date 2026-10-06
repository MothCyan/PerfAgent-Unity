namespace PerfAgent.Utils
{
    /// <summary>
    /// 「采集在跑、Profiler 面板却一帧都不写」时的**记录目标试探阶梯**（纯逻辑，离线可测）。
    ///
    /// <para><b>为什么要有它</b></para>
    /// 2026-10-07 现场：<c>enabled=True，profileEditor=False，firstFrameIndex=-1，lastFrameIndex=-1</c> ——
    /// 开关看着是开的，面板一帧都没写。而「Profiler 窗口左上角那个目标下拉」在
    /// <c>ProfilerDriver</c> 里到底由哪个字段表示、Play 模式下该是什么值，文档查不到，
    /// 靠猜已经猜错过一次（猜「必须强制 profileEditor=true」，但没有证据）。
    ///
    /// <para><b>做法：不要猜，按顺序试，并把哪一步有效写进日志</b></para>
    /// 面板一帧都没有时（此时清空帧历史是零代价），依次尝试：
    ///   0. 不动用户原来的目标（先等一小段，这也是绝大多数情况）；
    ///   1. 切「分析编辑器自身」（<c>profileEditor=true</c>）；
    ///   2. 切回「Play Mode / 设备」目标（<c>profileEditor=false</c>）。
    /// 每步观察 <see cref="StepSeconds"/> 秒，出现帧就停在这一步；全试完仍不出帧，
    /// 就明确说「不是目标的问题」，把注意力让给环境（内存告急、Profiler 被暂停、编辑器状态）。
    /// 有效的组合会写进快照备注 —— 报告里能看出这份数据是在哪种目标下录的。
    ///
    /// 纯逻辑放在这里：Unity 那边只负责「设开关 + 看帧号」，判定全在本类，可以被离线回归覆盖。
    /// </summary>
    internal static class RecordingTargetLadder
    {
        /// <summary>一共几步（含「不动」那一步）。</summary>
        public const int StepCount = 3;

        /// <summary>每一步观察多少秒才判「这一步没用」。</summary>
        public const double StepSeconds = 1.5;

        public enum Action
        {
            /// <summary>继续等（还不够时间，或者还没看到帧）。</summary>
            Wait,

            /// <summary>这一步出帧了 —— 停在这里。</summary>
            Accept,

            /// <summary>这一步没用，试下一步。</summary>
            Advance,

            /// <summary>所有组合都试过，确实不是记录目标的问题。</summary>
            GiveUp
        }

        /// <summary>
        /// 第 <paramref name="step"/> 步要把 <c>profileEditor</c> 设成什么；<c>null</c> = 不动（保持用户原设置）。
        /// </summary>
        public static bool? ProfileEditorFor(int step)
        {
            switch (step)
            {
                case 1: return true;    // 切「分析编辑器自身」
                case 2: return false;   // 切回「Play Mode / 设备」
                default: return null;
            }
        }

        public static string Describe(int step)
        {
            switch (step)
            {
                case 0: return "保持用户原来的记录目标";
                case 1: return "切到「分析编辑器自身」（profileEditor=true）";
                case 2: return "切回「Play Mode / 设备」目标（profileEditor=false）";
                default: return "未知步骤 " + step;
            }
        }

        /// <summary>
        /// 判定当前这一步该怎么办。
        /// </summary>
        /// <param name="step">当前步号（从 0 开始）。</param>
        /// <param name="secondsInStep">这一步已经观察了多少秒。</param>
        /// <param name="sawNewFrames">这一步期间面板有没有写出新的帧。</param>
        public static Action Decide(int step, double secondsInStep, bool sawNewFrames)
        {
            if (sawNewFrames) return Action.Accept;
            if (secondsInStep < StepSeconds) return Action.Wait;
            return step + 1 < StepCount ? Action.Advance : Action.GiveUp;
        }

        /// <summary>全部步骤都不出帧时，给用户的一句话结论（写进 Console 与快照备注）。</summary>
        public const string AllStepsFailed =
            "已经按顺序试过三种记录目标组合，面板仍然一帧都不写 —— 这不是「记录目标不对」的问题。"
            + "优先查这三件事：① 系统内存是否告急（Unity 会报 running out of memory，此时它自己会丢帧）；"
            + "② Profiler 窗口是不是被手动暂停了（Record 红点没亮）；"
            + "③ 编辑器是否刚做完编译/导入（此时帧循环还没稳定）。";
    }
}

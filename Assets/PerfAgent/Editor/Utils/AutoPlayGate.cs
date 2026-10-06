namespace PerfAgent.Utils
{
    /// <summary>
    /// 「点「跟随采集」后自动进 Play」的判定。纯逻辑，可离线回归。
    ///
    /// 抽出来的原因：这个动作会**替用户按一次 Play**，一旦判错（已经在 Play 里再按一次、
    /// 或者用户刚取消了却又被重新拉进 Play）就是「工具擅自控制编辑器」那一类体验事故，必须能逐条测。
    /// </summary>
    public static class AutoPlayGate
    {
        /// <param name="armed">跟随采集仍处于「待命」（没被取消、也没已经开始采集）</param>
        /// <param name="settingEnabled">设置里的「点采集后自动进入 Play」</param>
        /// <param name="isPlaying">当前已经在 Play</param>
        /// <param name="isPlayingOrWillChange">正在切进/切出 Play</param>
        public static bool ShouldEnterPlay(bool armed, bool settingEnabled, bool isPlaying, bool isPlayingOrWillChange)
        {
            if (!settingEnabled) return false;                     // 用户关掉了这个行为
            if (!armed) return false;                              // 期间被取消 / 已经采集中
            if (isPlaying || isPlayingOrWillChange) return false;  // 已经在 Play 或正在切 —— 别重复触发
            return true;
        }
    }
}

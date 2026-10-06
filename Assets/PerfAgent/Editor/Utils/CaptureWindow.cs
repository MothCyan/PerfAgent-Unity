namespace PerfAgent.Utils
{
    /// <summary>
    /// 采集窗口 [firstFrame, lastFrame] 的解析。纯逻辑，可离线回归。
    ///
    /// 这里修的是一个「采集永远收不到数据」的真故障：窗口起点是**开始采集那一刻**读的面板帧号
    /// （<c>ProfilerDriver.lastFrameIndex</c>），但上一轮采集结束时工具会把 Profiler 关掉并清空帧数据，
    /// 于是下一轮刚 <c>enabled = true</c> 时 <c>lastFrameIndex</c> 还是 <b>-1</b>；
    /// 而窗口起点只在这一刻算过一次，之后再不更新 —— 整段采集最后被判成
    /// 「采集还没开始就结束了」，Play 里玩的几十秒一帧都没读到。
    ///
    /// 口径：
    ///   - 有起点（≥0）：窗口 = 起点之后的第一帧 ~ 面板最新帧（只取属于本次采集的帧）
    ///   - 起点缺失（&lt;0，开始时面板还没有帧）：回退到「面板里最早的一帧」，并把起点标为**回推**
    ///     （快照里会写明，这种窗口可能包含采集开始前后的少量帧）
    ///   - 面板一帧都没有 / 采集期间没有新帧：返回 false，由调用方给出可定位的原因
    /// </summary>
    public static class CaptureWindow
    {
        /// <param name="pendingStartFrame">开始采集时记下的面板帧号；-1 = 那一刻面板还没有帧</param>
        /// <param name="firstAvailable">面板当前最早可读的帧号；-1 = 没有</param>
        /// <param name="lastAvailable">面板当前最新的帧号；-1 = 没有</param>
        /// <param name="firstFrame">窗口起点（含）</param>
        /// <param name="lastFrame">窗口终点（含）</param>
        /// <param name="startInferred">窗口起点是否为回推得到的</param>
        /// <returns>true = 拿到可用窗口；false = 没有任何可读帧</returns>
        public static bool TryResolve(int pendingStartFrame, int firstAvailable, int lastAvailable,
                                      out int firstFrame, out int lastFrame, out bool startInferred)
        {
            firstFrame = -1;
            lastFrame = -1;
            startInferred = false;

            if (lastAvailable < 0) return false;

            if (pendingStartFrame < 0)
            {
                if (firstAvailable < 0) return false;
                firstFrame = firstAvailable;
                lastFrame = lastAvailable;
                startInferred = true;
                return true;
            }

            int first = pendingStartFrame + 1;
            if (first > lastAvailable) return false;   // 采集期间面板没录到新帧

            firstFrame = first;
            lastFrame = lastAvailable;
            return true;
        }

        /// <summary>
        /// Profiler **会话重启**时把采集起点重定到面板里最早的一帧。
        ///
        /// 为什么要单独处理：面板帧号是**按 Profiling 会话**递增的，进/退 Play 或清空帧数据之后
        /// 会从头开始。而采集起点是「开始那一刻读到的帧号」，可能来自上一个会话（一个很大的旧号），
        /// 于是 <c>last - startFrame</c> 恒为负 → 界面上**一直显示「已记录 0 帧」**，玩多久都不动，
        /// 收尾时还会被判成「采集期间没有新帧」（实测就是用户看到的那个现象）。
        ///
        /// 判据：面板已有帧，但最新帧号**比起点还小** —— 帧号往回跳只可能是会话重启。
        /// 这种情况返回面板里最早的一帧当新起点（并让调用方把起点标为“回推”，快照里会写明）。
        /// </summary>
        /// <returns>重定后的起点；不需要重定时原样返回 pendingStartFrame</returns>
        public static int RebaseOnSessionRestart(int pendingStartFrame, int firstAvailable, int lastAvailable)
        {
            if (pendingStartFrame < 0) return pendingStartFrame;   // 还没起点，交给 TryResolve 处理
            if (lastAvailable < 0) return pendingStartFrame;       // 面板还没有帧，不能重定
            if (lastAvailable >= pendingStartFrame) return pendingStartFrame;

            return firstAvailable >= 0 ? firstAvailable : lastAvailable;
        }
    }
}

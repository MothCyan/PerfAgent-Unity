using System;
using UnityEditor;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// Profiler 开关的「借还」管理。
    ///
    /// <para><b>为什么必须集中管</b></para>
    /// 打开 Profiler 记录（尤其是 <c>profileEditor</c> —— 它让编辑器**每一帧**都被完整记录）之后，
    /// 只要没归还，编辑器就会一直录下去，Profiler 的帧数据持续膨胀：系统内存吃紧时
    /// Unity 会先自己丢数据、再报 <c>The system is running out of memory … Discarding profiler frames data.</c>
    ///
    /// <para><b>而「归还」这一步很容易被域重载吃掉</b></para>
    /// 进 Play 会触发域重载，静态字段连同挂在 <c>EditorApplication.update</c> 上的收尾回调一起消失，
    /// 收尾代码再也不会执行 —— 开关就永远留在「开」的状态。所以这里做三件事：
    ///   1. 借出时把原来的三个状态（<c>enabled</c> / <c>profileEditor</c> / <c>maxHistoryLength</c>）
    ///      写进 <c>SessionState</c>（跨域重载有效）；
    ///   2. 记录期间把面板历史长度压到 <see cref="HistoryFrames"/>，不让帧数据无限膨胀；
    ///   3. 新域加载时（<see cref="InitializeOnLoadMethod"/>）检查「借了没还」，立刻归还。
    /// </summary>
    internal static class ProfilerOwnership
    {
        const string OwnKey = "PerfAgent.Profiler.Owned";
        const string PrevEnabledKey = "PerfAgent.Profiler.PrevEnabled";
        const string PrevEditorKey = "PerfAgent.Profiler.PrevProfileEditor";
        const string PrevHistoryKey = "PerfAgent.Profiler.PrevHistoryLength";

        /// <summary>
        /// 我们记录期间允许的面板历史长度。
        ///
        /// 2000 帧足够装下一次跟随采集的分析窗口，同时把 Profiler 的内存占用钉在可控范围 ——
        /// 用户把历史上限设成几万帧时，照他的设置录会直接把内存吃光（实测见过系统级内存告警）。
        /// </summary>
        public const int HistoryFrames = 2000;

        /// <summary>当前是不是我们打开的（标记落在 SessionState，跨域重载有效）。</summary>
        public static bool Owned
        {
            get { try { return SessionState.GetBool(OwnKey, false); } catch { return false; } }
        }

        /// <summary>
        /// 借出：确保 Profiler 在记录。<paramref name="needProfileEditor"/> 为 true 时额外打开
        /// 「分析编辑器自身」—— 非 Play 模式下不开它的话，面板根本不记录编辑器帧
        /// （<c>lastFrameIndex</c> 不推进，读出来永远是 0 帧）。
        /// 用户自己开着 Profiler 时也照常记录，只额外压一下历史上限，归还时不动他的开关。
        /// </summary>
        public static void Acquire(bool needProfileEditor)
        {
            if (!Owned)
            {
                bool prevEnabled = false, prevEditor = false;
                int prevHistory = 0;
                try
                {
                    prevEnabled = ProfilerApi.Enabled;
                    prevEditor = ProfilerApi.ProfileEditor;
                    prevHistory = ProfilerApi.MaxHistoryLength;
                }
                catch { }

                try
                {
                    SessionState.SetBool(PrevEnabledKey, prevEnabled);
                    SessionState.SetBool(PrevEditorKey, prevEditor);
                    SessionState.SetInt(PrevHistoryKey, prevHistory);
                    SessionState.SetBool(OwnKey, true);
                }
                catch { }
            }

            try
            {
                if (!ProfilerApi.Enabled) ProfilerApi.Enabled = true;
                if (needProfileEditor && !ProfilerApi.ProfileEditor) ProfilerApi.ProfileEditor = true;
                if (ProfilerApi.MaxHistoryLength > HistoryFrames) ProfilerApi.MaxHistoryLength = HistoryFrames;
            }
            catch { }
        }

        /// <summary>
        /// 归还：还原借用前的状态；如果这次记录是我们开的，顺便清掉录下来的帧数据
        /// （除了占内存没别的用处 —— 需要的数据在归还前已经读完了）。
        ///
        /// 不是我们借的就什么都不做：不能替用户关掉他自己开着的 Profiler，
        /// 更不能去清他的数据（那可能正是他在看的东西）。
        /// </summary>
        public static void Release()
        {
            if (!Owned) return;

            try
            {
                int prevHistory = SessionState.GetInt(PrevHistoryKey, 0);
                if (prevHistory > 0) ProfilerApi.MaxHistoryLength = prevHistory;

                ProfilerApi.ProfileEditor = SessionState.GetBool(PrevEditorKey, false);

                bool prevEnabled = SessionState.GetBool(PrevEnabledKey, false);
                ProfilerApi.Enabled = prevEnabled;

                if (!prevEnabled) ProfilerApi.ClearAllFrames();
            }
            catch { }
            finally
            {
                try { SessionState.SetBool(OwnKey, false); } catch { }
            }
        }

        /// <summary>
        /// 新域兜底归还 —— 域重载会吃掉收尾回调，这里是唯一能保证「借了必还」的地方。
        /// </summary>
        [InitializeOnLoadMethod]
        static void RestoreAbandoned()
        {
            if (!Owned) return;

            Release();
            Debug.LogWarning("[PerfAgent] 上一次会话没有归还 Profiler 开关（多半是进 Play 时的域重载打断了收尾），"
                + "已自动还原成原来的设置，避免编辑器一直逐帧记录把内存吃光。");
        }
    }
}

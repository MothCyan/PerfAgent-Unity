using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using PerfAgent.Collectors;
using PerfAgent.Utils;

namespace PerfAgent.Core
{
    /// <summary>
    /// 跟随采集：**你自己操作，PerfAgent 在旁边记录**。
    ///
    /// 这是本插件拿到运行时数据的唯一入口：
    ///   你自己进 Play、自己玩、自己退出，**时长不限**。
    ///     采到的是真实操作过程 —— 战斗、开背包、切界面、加载，全是真实发生的行为，
    ///     而不是脚本模拟出来的。脚本永远演不出「玩家的操作节奏」。
    ///
    /// 核心设计：**只在用户按下采集时动一次 Play**。具体三件事：
    ///   1. 用户点「跟随采集」后，先量一次编辑器开销基线（只能在编辑模式量），
    ///      然后**替你按一次 Play** 并开始采集（不想让它代劳就在设置里关掉）——
    ///      实测里「点完采集忘了按 Play」是白采一整轮的高频原因；
    ///   2. 退出 Play 时自动收尾出快照 —— 那时人已经玩完了；
    ///   3. 中途也可以随时手动停止（推荐这样：在 Play 里点停止，采集器能拿到完整的资源数据）。
    ///
    /// 边界：**只有人类按钮会进 Play**。MCP / Agent 侧没有任何进 Play 的入口
    ///（见 `SopDefinition.HardRules`），所以外部模型仍然不能自己跑一次采集。
    ///
    /// 为什么收尾必须在 ExitingPlayMode 那一帧同步做完：退出 Play 会销毁域，
    /// 所有静态状态连同帧缓冲一起消失，之后再想保存就没数据了。
    /// </summary>
    public static class FollowCapture
    {
        const string SessionKey = "PerfAgent.FollowCapture.Armed";

        /// <summary>
        /// 单次跟随采集的帧数硬上限。
        ///
        /// 「时长不限」不等于「内存不限」：以前这里是 100 万帧（因为要自己存下每一帧），
        /// 现在数据直接读 Profiler 面板，真正的上限是**面板自己的帧历史长度**（默认约两千帧），
        /// 超过就被面板丢掉了 —— 这个常量只是安全阀。
        /// </summary>
        public const int MaxFrames = 20000;

        static PanelCapture _capture;
        static bool _armed;

        /// <summary>已待命，等下一次进入 Play。</summary>
        public static bool Armed { get { return _armed; } }

        /// <summary>正在采集。</summary>
        public static bool Capturing { get { return _capture != null && _capture.running; } }

        /// <summary>已采帧数（界面用来显示实时进度）。</summary>
        public static int CapturedFrames { get { return _capture == null ? 0 : _capture.CapturedCount; } }

        /// <summary>最近一次收尾产出的快照 id。</summary>
        public static string LastSnapshotId = "";

        public static event Action Changed;

        static void Notify()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        // =====================================================================
        // 启动 / 停止
        // =====================================================================

        /// <summary>进入待命：你下次进 Play 时自动开始采集。</summary>
        public static bool Arm(out string error)
        {
            error = null;

            if (_armed || Capturing)
            {
                error = "跟随采集已经在运行了。";
                return false;
            }
            if (PerfSession.Capturing)
            {
                error = "已有抓帧任务在进行中，等它结束再开始跟随采集。";
                return false;
            }

            _armed = true;
            SessionState.SetBool(SessionKey, true);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // 趁现在还在编辑模式，量一次「编辑器空闲开销基线」。
            // 不量的话，「每帧托管分配」里编辑器自身的开销会被当项目的分配
            //（一个空工程也能报出上百 KB/帧）；而一旦进了 Play 就再也测不准了 ——
            // 所以「自动进 Play」必须等它量完（回调里再按 Play，见 TryAutoEnterPlay）。
            EditorOverheadBaseline.Measure(delegate { TryAutoEnterPlay(); });

            // 已经在 Play 里的话立刻开始 —— 不让用户为了开始采集先退出再重进一次
            if (EditorApplication.isPlaying) BeginCapture();

            Notify();
            return true;
        }

        /// <summary>
        /// 基线量完后自动进 Play —— 用户点「跟随采集」就不必再自己按一次。
        ///
        /// 实测里「点完采集忘了按 Play」是白采一整轮的高频原因。判定放在
        /// <see cref="PerfAgent.Utils.AutoPlayGate"/>（纯逻辑、有离线回归）：
        /// 设置里关掉、期间被取消、已经在 Play / 正在切换 —— 一律不动手。
        /// </summary>
        static void TryAutoEnterPlay()
        {
            bool allowed = true;
            try { allowed = PerfAgentSettings.Config.autoPlayOnFollowCapture; }
            catch { }

            if (!AutoPlayGate.ShouldEnterPlay(_armed, allowed,
                    EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode)) return;

            Debug.Log("[PerfAgent] 编辑器开销基线已量好，自动进入 Play 开始采集（可在 PerfAgent 设置里关掉「点采集后自动进入 Play」）。");
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// 手动结束。
        /// 待命状态下 = 放弃；采集中的状态下 = 立刻收尾（但**不会**退出 Play，你可以接着玩）。
        /// </summary>
        public static bool Stop(out string error)
        {
            error = null;

            if (_armed && _capture == null)
            {
                Disarm();
                Notify();
                return true;
            }

            if (_capture == null)
            {
                error = "当前没有进行中的跟随采集。";
                return false;
            }

            Finish("用户手动结束采集");
            return true;
        }

        // =====================================================================
        // Play 模式来去
        // =====================================================================

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (_armed) BeginCapture();
                return;
            }

            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // 人已经玩完了，退出 Play 就是「结束」的信号。
                // 必须在这一帧同步收尾：再往后域就被销毁了，帧缓冲会跟着消失。
                if (_capture != null) Finish("用户退出了 Play 模式，采集结束");
            }
        }

        static void BeginCapture()
        {
            if (_capture != null || PerfSession.Capturing) return;

            _armed = false;

            var capture = new PanelCapture(MaxFrames, delegate (PanelCaptureData data)
            {
                Complete(data);
            }, null);

            // 丢掉进入 Play 的头 1 秒：域重载 + 首次 Shader 编译 + 资源初始化都集中在那里，
            // 实测空场景第 2 帧就有 87 ms —— 那是引擎/编辑器的启动成本，不是项目的每帧开销。
            capture.warmupSeconds = 1.0;

            _capture = capture;
            PerfSession.Capturing = true;
            capture.Start();

            Notify();
        }

        static void Finish(string reason)
        {
            var capture = _capture;
            if (capture == null) return;

            // FinishNow 会同步回调 Complete —— 摘要、分析、落盘都在这一帧内做完，
            // 不依赖任何后续的编辑器更新（退出 Play 时就靠这一点）
            capture.FinishNow();

            if (_capture != null)
            {
                // Complete 里因为异常没能清干净，兜底
                _capture = null;
                PerfSession.Capturing = false;
                Disarm();
                Debug.LogWarning("[PerfAgent] 跟随采集收尾异常：" + reason);
            }
        }

        // =====================================================================
        // 收尾
        // =====================================================================

        static void Complete(PanelCaptureData data)
        {
            _capture = null;
            PerfSession.Capturing = false;
            Disarm();

            try
            {
                var snap = PerfPipeline.CreateSnapshot("跟随采集（自己操作）");

                try { snap.scenePath = SceneManager.GetActiveScene().path; } catch { }

                PanelCapture.Summarize(snap, data);

                snap.AddNote("数据来自「跟随采集」：由你自己操作，采集从进入 Play 开始、"
                    + "到退出 Play（或手动停止）结束，没有固定时长。"
                    + "因此这份数据包含真实操作过程里的全部阶段 —— 战斗、开界面、切场景、加载 —— "
                    + "看结论时请把阶段性尖峰一并纳入判断，而不是只盯平均值。");

                // 采集器里有依赖 AssetDatabase 的，退出 Play 那一帧可能取不到数据；
                // 各自的 try 会把它写成 note，不会中断收尾。
                PerfPipeline.RunCollectors(snap, false, null);
                PerfPipeline.Analyze(snap);

                PerfPipeline.SaveAndSetCurrent(snap);
                LastSnapshotId = snap.id;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 跟随采集收尾失败: " + e.Message);
            }

            // 本次没能测到基线的话，趁现在（已回到编辑模式）补测一次，下次采集就能扣掉编辑器开销。
            double baselineValue;
            string baselineWhy;
            if (!EditorOverheadBaseline.TryGet(out baselineValue, out baselineWhy))
                EditorOverheadBaseline.Measure(null);

            Notify();
        }

        static void Disarm()
        {
            _armed = false;
            SessionState.EraseBool(SessionKey);
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        /// <summary>
        /// 每个新域都会跑一次。
        ///
        /// 这是跟随采集能工作的关键：进入 Play 会触发域重载，把所有静态字段清空，
        /// 连同 playModeStateChanged 的订阅一起丢掉。如果不在这里恢复，
        /// 「待命」状态会在用户进 Play 的瞬间失效，采集永远不会开始。
        /// </summary>
        [InitializeOnLoadMethod]
        static void Bootstrap()
        {
            if (!SessionState.GetBool(SessionKey, false)) return;

            _armed = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // 重载后已经在 Play 里（正是「进入 Play」引发的那次重载）→ 直接开采
            if (EditorApplication.isPlaying) BeginCapture();
        }
    }
}

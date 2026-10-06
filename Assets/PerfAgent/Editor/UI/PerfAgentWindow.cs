using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PerfAgent.Agent;
using PerfAgent.Analysis;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.UI
{
    /// <summary>性能诊断面板：采集 / 结论 / 明细 / 对话，全部在一个可停靠窗口内。</summary>
    public class PerfAgentWindow : EditorWindow
    {
        const string MenuRoot = "Tools/PerfAgent/";

        /// <summary>布局 */
        Label _status;
        /// <summary>主面板上的「启用 AI」开关（与配置窗口的「纯本地模式」是同一个开关的两面）。</summary>
        Toggle _aiToggle;
        VisualElement _liveHost;
        VisualElement[] _liveBars;
        Label _liveLabel;
        /// <summary>预算表单的容器：恢复默认后要整体重建，否则输入框里还是旧值。</summary>
        VisualElement _budgetHost;
        ScrollView _snapshotHost;
        ScrollView _detailHost;
        Label _transcript;
        ScrollView _transcriptScroll;
        TextField _input;
        Button _sendButton;
        /// <summary>
        /// 跟随采集状态栏的限流。
        ///
        /// PollFollowCapture 挂在 EditorApplication.update 上（每个编辑器帧都会跑），
        /// 采集期间逐帧拼字符串 + 赋值 Label.text 会触发界面重排 —— 这些分配会被
        /// 「GC Allocated In Frame」计数器算进去，**工具等于在污染自己的测量结果**。
        /// 所以状态栏最多每 0.25 秒、且帧数变化时才刷一次。
        /// </summary>
        double _nextFollowPoll;
        int _lastFollowFrames = -1;

        /// <summary>采集期间的实时帧率曲线（采集开始才开始填，结束后冻结保留）。</summary>
        readonly CaptureLiveStats _live = new CaptureLiveStats();
        bool _liveActive;

        // ---- 采集期间的「细条」模式 ----
        //
        // 跟随采集时面板常常就飘在 Game 视图旁边，整块面板会把画面挡掉一半 ——
        // 实际操作就是「一按采集就把窗口拖到边上、缩到最小」。所以采集一开始就自动收成一条
        // 只显示帧率/帧耗时的细条（带「展开面板」「停止采集」两个按钮），采集结束再自动展开。
        //
        // 两个容易踩的点（都写了注释在方法上）：minSize 没临时改小的话浮动窗口不肯缩；
        // 停靠的窗口写 position 不生效，这时只能把内容收起来、并在细条上告诉用户怎么才能真缩小。
        VisualElement _strip;
        Label _stripLabel;
        bool _stripped;
        Rect _preStripRect;
        Vector2 _preStripMinSize;

        /// <summary>尺寸自愈的限流时间点；以及「已报过一次被撑大」的标记。</summary>
        double _nextSizeCheck;
        bool _warnedWindowGrown;
        /// <summary>本次采集已经自动收过一次 —— 用户手动展开后不再自动收起。</summary>
        bool _stripDoneForThisCapture;
        /// <summary>停靠的窗口写 position 不生效；探测结果要写在细条上。</summary>
        bool _positionWritable = true;
        /// <summary>连续「0 帧」的起点（EditorApplication.timeSinceStartup）；0 = 当前没在数。</summary>
        double _stripZeroSince;
        /// <summary>本次采集是否已经报过「一直没帧」的详细诊断（只报一次，不刷屏）。</summary>
        bool _warnedZeroFramesLive;
        /// <summary>细条模式下根容器的内边距（展开时要还原）。</summary>
        Vector4 _preStripPadding;

        /// <summary>会话的原始 Markdown；显示时统一转成富文本（否则 `**粗体**` 会原样显示星号）。</summary>
        readonly StringBuilder _transcriptRaw = new StringBuilder();

        VisualElement _tabRow;
        /// <summary>LLM 配置状态条：让用户不用去 Project Settings 就能看到当前状态并就地配置。</summary>
        Label _llmStatus;

        /// <summary>
        /// 不要在这里写 `new AgentLoop()`：EditorWindow 的实例字段初始化器运行在
        /// ScriptableObject 构造函数内部，那时任何对 ScriptableSingleton 的读取都会被 Unity 拒绝
        /// （UnityException: LoadSerializedFileAndForget is not allowed…），
        /// 并且会导致字段初始化失败、OnEnable 里抛 NullReferenceException。
        /// 因此改为首次使用时惰性创建。
        /// </summary>
        AgentLoop _agent;

        AgentLoop Agent
        {
            get
            {
                if (_agent == null) _agent = new AgentLoop();
                return _agent;
            }
        }

        string _activeTab = "findings";
        /// <summary>对比标签页选中的基准快照路径；为空时自动取最近一份非当前快照。</summary>
        string _baselinePath = "";
        /// <summary>当前会话 id（= 持久化文件名）。为空表示尚未保存过。</summary>
        string _conversationId = "";
        /// <summary>正在等回答（防止重复发送）。不用按钮的 enabled 状态兼职：见 SetSending。</summary>
        bool _sending;

        [MenuItem(MenuRoot + "打开性能诊断面板 %#p", false, 100)]
        public static void Open()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.minSize = new Vector2(StripGeometry.PanelWidth, StripGeometry.PanelHeight);
            // 用户明确要求「每次打开固定高度」：打开就走一遍固定尺寸（必要时换成浮动窗口）。
            window.ApplyFixedWindowSize(true);
            window.Show();
        }

        [MenuItem(MenuRoot + "静态审计（不抓帧）", false, 102)]
        public static void StaticAudit()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.RunStaticAudit();
        }

        [MenuItem(MenuRoot + "API 探针", false, 103)]
        public static void OpenProbe()
        {
            PerfApiProbeWindow.Open();
        }

        /// <summary>
        /// 跟随采集：**你先按一下，工具负责进 Play 并记录**，你只管玩，时长不限。
        ///
        /// 按钮行为随状态变：
        ///   未开始 → 量基线后自动进 Play 并开始记录（待命期间再点一次 = 取消）
        ///   采集中 → 结束并立即出快照（不会退出 Play，你可以接着玩）
        /// </summary>
        [MenuItem(MenuRoot + "跟随采集（自己操作，时长不限）", false, 105)]
        public static void FollowCaptureMenu()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.ToggleFollowCapture();
        }

        /// <summary>
        /// 把面板收成一条只显示帧率的细条 / 展开。
        ///
        /// 采集开始时会自动收起（见 PollFollowCapture），这个菜单是给手动场景用的：
        /// 比如你想一边看波形一边看 Game 视图，或想把面板收回正常尺寸。
        /// </summary>
        [MenuItem(MenuRoot + "面板：收起为细条 / 展开", false, 106)]
        public static void StripMenu()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.ToggleStrip();
        }

        public void ToggleStrip()
        {
            if (_strip == null)
            {
                SetStatus("面板还在初始化，稍后再点一次。");
                return;
            }
            SetStripped(!_stripped, true);
        }

        public void ToggleFollowCapture()
        {
            string error;

            if (FollowCapture.Capturing)
            {
                if (!FollowCapture.Stop(out error)) { SetStatus(error); return; }

                SetStatus("跟随采集已结束，快照 " + FollowCapture.LastSnapshotId + " 已生成。");
                RefreshSnapshots();
                RefreshDetails();
                AppendTranscript(SummarizeForChat(PerfSession.Current));
                return;
            }

            if (FollowCapture.Armed)
            {
                FollowCapture.Stop(out error);
                SetStatus("已取消跟随采集。");
                return;
            }

            if (!FollowCapture.Arm(out error))
            {
                SetStatus("无法开始跟随采集：" + error);
                return;
            }

            SetStatus(EditorApplication.isPlaying
                ? "跟随采集已开始 —— 你继续操作，想结束时再点一次这个按钮（或直接退出 Play）。"
                : (PerfAgentSettings.Config.autoPlayOnFollowCapture
                    ? "跟随采集已待命：正在量编辑器开销基线（约 1~4 秒，只能在编辑模式量），随后**自动进入 Play** 开始记录 —— "
                      + "玩 5~10 秒后退出 Play 即自动结束并出结论。（不想自动进 Play：设置里关掉「点采集后自动进入 Play」）"
                    : "跟随采集已待命 —— 现在进入 Play 就会自动开始记录，时长不限（自动进 Play 已在设置里关掉）。"));
        }

        void OnEnable()
        {
            // minSize 以前只在 Open() 里设过。而窗口被 Unity 从布局恢复时不走 Open()，
            // 于是可以恢复成一个很小的尺寸：两栏被挤扁、卡片内容互相重叠（实测就是这样）。
            // 这里也设一遍，并**主动撑一下**：minSize 只限制手动拖拽，
            // 管不住「从布局里恢复成小窗口」与「细条来回后 minSize 被域重载清掉」这两种情况
            //（实测用户截图：整个面板只剩一条监视行，字还被截到「口径」）。
            minSize = new Vector2(StripGeometry.PanelWidth, StripGeometry.PanelHeight);
            // 每次 Unity 会话允许自动升级一次（把停靠窗口换成浮动窗口）——
            // 不限制的话，每次进/出 Play 的域重载都会去动用户的布局。
            ApplyFixedWindowSize(true);

            PerfSession.Changed += OnSessionChanged;
            PerfAgentSettingsWindow.Changed += RefreshLlmStatus;
            Agent.OnStatus += SetStatus;
            EditorApplication.update += PollFollowCapture;
            FollowCapture.Changed += OnFollowCaptureChanged;
        }

        /// <summary>
        /// 把面板撑回可用尺寸，并**回读确认**（不碰细条状态）。
        ///
        /// 为什么需要「主动撑」而不是只设 minSize：minSize 只约束手动拖拽。窗口尺寸是持久化的，
        /// 一旦被存成小尺寸（或细条收/展切换时 minSize 被域重载清掉），它就会一直小下去，
        /// 而内容只会被裁掉 —— 用户看到的就是「面板只剩一条监视行」。
        ///
        /// 为什么要回读：实测「还是横着一条」—— 窗口是**停靠**或**最大化**状态时，
        /// Unity 根本不接受程序写进来的 position（尺寸由布局管）。不回读就会一直谎报「已撑开」，
        /// 用户那边却纹丝不动，排查方向全错。所以这里把真实结果写出来（含「该怎么办」）。
        /// </summary>
        void EnsureUsableWindowSize()
        {
            if (_stripped) return;   // 细条期间绝不能撑
            try
            {
                var p = position;
                if (!StripGeometry.NeedsGrow(p.width, p.height)) return;

                float w = StripGeometry.Grow(p.width, StripGeometry.PanelWidth);
                float h = StripGeometry.Grow(p.height, StripGeometry.PanelHeight);
                position = new Rect(p.x, p.y, w, h);

                // 回读：拿不到请求的尺寸 = 这个窗口不归程序管（停靠 / 最大化）
                var after = position;
                bool ok = Mathf.Abs(after.width - w) < 2f && Mathf.Abs(after.height - h) < 2f;

                if (!_warnedWindowGrown)
                {
                    _warnedWindowGrown = true;
                    Debug.Log(ok
                        ? ("[PerfAgent] 面板被恢复成了不可用的小尺寸（" + Fmt(p) + "），已撑到 " + Fmt(after)
                           + "。minSize 只约束手动拖拽，管不住「从布局恢复」与「细条来回」，所以这里主动校正。")
                        : ("[PerfAgent] 面板尺寸不可用（" + Fmt(p) + "），但程序改不动它：请求 "
                           + w.ToString("0") + "x" + h.ToString("0") + "，实际读回 " + Fmt(after)
                           + "。这说明窗口是**停靠 / 最大化**状态（Unity 的尺寸由布局管，写 position 无效）。"
                           + "请把它拖出来变成浮动窗口、或拖动分隔条给它更多高度；也可以用菜单"
                           + " Tools/PerfAgent/窗口：恢复可用尺寸 再看一次结果。"));
                }
            }
            catch { }
        }

        /// <summary>
        /// 尺寸自愈的限流包装：每个轮询周期校验一次就够（写 position 会触发窗口重排，按秒限流）。
        ///
        /// 为什么不能只靠 OnEnable / 细条展开两条路径（实测反馈「现在还是这样，往下拉一点啊」）：
        /// 面板被存成「宽而扁」后，OnEnable 早已跑过、也没再经过细条切换 —— 它就那么扁着，
        /// 只有持续校验才会被纠正回来。
        /// </summary>
        void EnsureUsableWindowSizeThrottled()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextSizeCheck) return;
            _nextSizeCheck = now + 1.0;
            EnsureUsableWindowSize();
        }

        static string Fmt(Rect r)
        {
            return r.width.ToString("0") + "x" + r.height.ToString("0");
        }

        static bool SizeOk(Rect r)
        {
            return Mathf.Abs(r.width - StripGeometry.PanelWidth) < 2f
                && Mathf.Abs(r.height - StripGeometry.PanelHeight) < 2f;
        }

        static Vector2 FixedPanelSize
        {
            get { return new Vector2(StripGeometry.PanelWidth, StripGeometry.PanelHeight); }
        }

        /// <summary>「本次 Unity 会话已经为面板换过一次浮动窗口」的标记（避免每次域重载都动布局）。</summary>
        const string EscalatedKey = "PerfAgent.UI.EscalatedToFloating";

        /// <summary>
        /// 把面板设成**固定尺寸**（用户要求：每次打开固定高度）；写不动就升级成浮动窗口再写。
        ///
        /// 为什么必须升级成「换浮动」（实测第 3 轮反馈「还是不行，要让它每次打开固定高度」）：
        /// 窗口处于**停靠**状态时，尺寸完全由 dock 布局决定 —— `minSize` 与 `position` 都会被忽略，
        /// 代码没有任何办法改它的高度。不换成浮动窗口，它就永远是那条横线。
        /// 升级一定写 Console + 状态栏，不做静默改布局；且**每次 Unity 会话只升级一次**，
        /// 免得每次进/出 Play 的域重载都去动用户的布局。
        /// </summary>
        void ApplyFixedWindowSize(bool escalate)
        {
            if (_stripped) return;
            try
            {
                var want = FixedPanelSize;
                var before = position;
                position = new Rect(before.x, before.y, want.x, want.y);
                var after = position;

                if (SizeOk(after))
                {
                    SetStatus("面板尺寸固定为 " + want.x.ToString("0") + "x" + want.y.ToString("0") + "。");
                    return;
                }

                bool canEscalate = escalate && !SessionState.GetBool(EscalatedKey, false);
                if (!canEscalate)
                {
                    if (!_warnedWindowGrown)
                    {
                        _warnedWindowGrown = true;
                        Debug.Log("[PerfAgent] 面板尺寸写不动（请求 " + want.x.ToString("0") + "x" + want.y.ToString("0")
                            + "，实际读回 " + Fmt(after) + "）：这是**停靠**窗口，尺寸由 dock 布局决定，"
                            + "minSize 与 position 都会被 Unity 忽略。用菜单 Tools/PerfAgent/窗口：恢复可用尺寸 "
                            + "可以把它一键换成浮动窗口。");
                    }
                    return;
                }

                SessionState.SetBool(EscalatedKey, true);
                Debug.Log("[PerfAgent] 面板尺寸写不动（请求 " + want.x.ToString("0") + "x" + want.y.ToString("0")
                    + "，实际读回 " + Fmt(after) + "）：停靠窗口的尺寸由 dock 布局决定，代码改不了 —— "
                    + "已把它换成浮动窗口，让「每次打开固定高度」真正生效。");
                try { ShowUtility(); } catch { }
                position = new Rect(before.x, before.y, want.x, want.y);

                // 换浮动后窗口要重排，可能到下一帧才接受尺寸 —— 下一帧复核一次再下结论。
                EditorApplication.delayCall += delegate
                {
                    try
                    {
                        var p = position;
                        if (!SizeOk(p)) position = new Rect(p.x, p.y, want.x, want.y);
                        var a2 = position;
                        string msg = SizeOk(a2)
                            ? ("面板已固定为 " + want.x.ToString("0") + "x" + want.y.ToString("0") + "（浮动窗口）。")
                            : ("面板尺寸仍然改不动（读回 " + Fmt(a2) + "）：请手动把「性能诊断」标签拖出停靠区，"
                               + "或拖动分隔条 —— 在停靠区域里，任何代码都改不了窗口高度。");
                        SetStatus(msg);
                        Debug.Log("[PerfAgent] " + msg);
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// <summary>
        /// 菜单：强制把面板设成可用尺寸，并把**真实结果**说出来。
        ///
        /// 存在的意义（实测反馈「还是横着一条」）：窗口停靠 / 最大化时，Unity 不接受程序改尺寸 ——
        /// 这不是 bug 也不是工具问题，但用户看不到这个区别，只会以为「喊了几次你没改」。
        /// 所以给一个可以当场验证的入口：点一下，状态栏与 Console 会直接告诉你
        /// 「已经是 900x600」或者「读回来还是 1520x60 —— 请把它拖成浮动窗口 / 拖分隔条」。
        /// </summary>
        [MenuItem(MenuRoot + "窗口：恢复可用尺寸", false, 108)]
        public static void ForceUsableSize()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.minSize = new Vector2(StripGeometry.PanelWidth, StripGeometry.PanelHeight);

            if (window._stripped) window.SetStripped(false, true);

            // 菜单是「人来明确要求」的入口：允许它把停靠窗口换成浮动窗口
            //（否则停靠状态下这个菜单什么也做不了，用户只会觉得「点了没用」）。
            SessionState.SetBool(EscalatedKey, false);
            window.ApplyFixedWindowSize(true);
        }

        void OnDisable()
        {
            SaveConversation();
            PerfSession.Changed -= OnSessionChanged;
            PerfAgentSettingsWindow.Changed -= RefreshLlmStatus;
            if (_agent != null) _agent.OnStatus -= SetStatus;
            EditorApplication.update -= PollFollowCapture;
            FollowCapture.Changed -= OnFollowCaptureChanged;
        }

        void OnFollowCaptureChanged()
        {
            _nextFollowPoll = 0;   // 立刻刷新一次状态栏，不等下一个轮询周期
        }

        void OnSessionChanged()
        {
            RefreshSnapshots();
            RefreshDetails();
        }

        // =====================================================================
        // 布局
        // =====================================================================

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = Theme.WindowBg;
            Theme.Pad(root, 10, 10, 8, 8);

            // ---- 顶部：标题 + LLM 状态 + 工具条 ----
            var header = Theme.Header("性能诊断", "跟随采集 → 实时波形 → 结论与修复计划；改工程前一律要你点确认");

            _aiToggle = new Toggle("启用 AI");
            _aiToggle.style.marginRight = 8;
            _aiToggle.style.fontSize = Theme.SizeSmall;
            _aiToggle.tooltip = "开 = 允许把工具结论交给 LLM 做对话追问；关 = 纯本地模式（不外传任何数据）。\n"
                + "规则引擎的结论、证据链与修复计划不受这个开关影响。";
            _aiToggle.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { SetAiEnabled(e.newValue); });
            header.Add(_aiToggle);

            _llmStatus = Theme.Pill("AI：检查中…", Theme.TextDim);
            header.Add(_llmStatus);
            header.Add(Theme.Ghost("LLM 配置", PerfAgentSettingsWindow.Open));
            // ---- 顶部这一串不允许被压扁 ----
            //
            // UI Toolkit 在纵向空间不够时，会把 flexShrink=1 的子元素压到比内容还小，
            // 而它不会裁剪 —— 结果是标签叠字、输入框被压成一条黑边。
            // 宁可让下面两块（左栏/明细）去滚动，也不让标题、工具条、状态行变形。
            header.style.flexShrink = 0;
            root.Add(header);

            var toolbar = BuildToolbar();
            toolbar.style.flexShrink = 0;
            root.Add(toolbar);
            root.Add(Theme.Divider());

            // ---- 状态行 ----
            _status = new Label("就绪");
            _status.style.fontSize = Theme.SizeSmall;
            _status.style.marginTop = 2;
            _status.style.marginBottom = 2;
            _status.style.color = Theme.TextDim;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.flexShrink = 0;
            root.Add(_status);

            _liveHost = BuildLiveStrip();
            _liveHost.style.display = DisplayStyle.None;   // 有样本了才显示
            _liveHost.style.flexShrink = 0;
            root.Add(_liveHost);

            // ---- 两栏 ----
            var columns = new VisualElement();
            columns.style.flexDirection = FlexDirection.Row;
            columns.style.flexGrow = 1;
            columns.style.flexShrink = 1;
            columns.style.marginTop = 6;
            root.Add(columns);

            // 左栏：标签 / 快照 / 预算。
            //
            // 这一栏必须能滚：三张卡片的自然高度加起来（标签 + 快照列表 + 预算表单）比窗口高是常态，
            // 而纵向空间不够时卡片会被压扁 —— 实测就是这样：预算卡里的行叠在一起、
            // 输入框被压成一条黑杠、标签页被裁掉半行。
            var leftScroll = new ScrollView(ScrollViewMode.Vertical);
            leftScroll.style.width = 300;
            // 允许收缩到一个能看的宽度：minSize 对「停靠」的窗口是不生效的（停靠尺寸由布局决定），
            // 所以窄窗口下必须自己能挤，而不是把右栏（连带发送按钮）顶出窗口外。
            leftScroll.style.minWidth = 240;
            leftScroll.style.flexShrink = 1;
            leftScroll.style.marginRight = 10;
            columns.Add(leftScroll);

            var left = leftScroll.contentContainer;
            left.style.flexDirection = FlexDirection.Column;

            var tabsCard = Theme.Card("分析标签", "按维度看明细");
            _tabRow = new VisualElement();
            _tabRow.style.flexDirection = FlexDirection.Row;
            _tabRow.style.flexWrap = Wrap.Wrap;
            tabsCard.Add(_tabRow);
            BuildTabs();
            left.Add(tabsCard);

            var snapCard = Theme.Card("快照", "历史分析结果");
            _snapshotHost = new ScrollView(ScrollViewMode.Vertical);
            _snapshotHost.style.maxHeight = 180;
            _snapshotHost.style.flexShrink = 0;
            snapCard.Add(_snapshotHost);
            left.Add(snapCard);

            var budgetCard = Theme.Card("性能预算", "改完即时重算");
            _budgetHost = new VisualElement();
            _budgetHost.Add(BuildBudgetEditor());
            budgetCard.Add(_budgetHost);
            left.Add(budgetCard);

            // 右栏：明细 + 会话
            var right = new VisualElement();
            right.style.flexGrow = 1;
            right.style.flexShrink = 1;
            // 最小宽度给得很克制：给太大（原来 360）会让整排超出窗口，
            // 结果是卡片右侧（正是发送按钮所在的位置）被裁到窗口外。
            right.style.minWidth = 180;
            right.style.flexDirection = FlexDirection.Column;
            columns.Add(right);

            _detailHost = new ScrollView(ScrollViewMode.Vertical);
            // 明细区比对话区“让一点”：阅读结论是一阵子的事，而对话是要边看边问的。
            // 2:3 的分法（flexGrow）比 1:1 好用 —— 实测空明细区占掉半屏、对话却被挤成一条。
            _detailHost.style.flexGrow = 2;
            _detailHost.style.minHeight = 140;
            _detailHost.style.backgroundColor = Theme.SunkenBg;
            Theme.Rounded(_detailHost, Theme.Radius);
            Theme.Border1(_detailHost, Theme.Border);
            Theme.Pad(_detailHost, 10, 10, 8, 8);
            right.Add(_detailHost);

            right.Add(BuildChatPanel());

            RefreshSnapshots();
            RefreshDetails();
            RefreshLlmStatus();
            AppendTranscript("**性能诊断 Agent**\n\n点「跟随采集」后**工具会自动量一次基线并替你进入 Play**，"
                + "你只管操作（战斗、开背包、切界面都算）；面板会**自动收成一条只显示帧率的细条**，不会挡住 Game 视图。\n"
                + "玩 5~10 秒后**退出 Play**（或点细条上的「停止采集」）即自动结束、面板自动展开。\n"
                + "想一边看波形一边操作，点细条上的「展开面板」（或菜单 `Tools/PerfAgent/面板：收起为细条 / 展开`）就行；"
                + "不想让工具替你按 Play，就在设置里关掉「点采集后自动进入 Play」。\n"
                + "然后可以直接提问，例如：\n- 为什么会有周期性卡顿？\n- 内存的大头在哪里？\n- 每帧的分配是从哪来的？\n"
                + "\n要贴给别人（或丢给外部 AI 继续追问），点工具栏「复制结论」。\n");
            RestoreLatestConversation();

            // 细条：只有它一直待在根上，默认隐藏（见 SetStripped）。
            // 放在最后是因为 SetStripped 要遍历根节点的子元素，先建完全部布局再建它最直观。
            _strip = BuildStrip();
            _strip.style.display = DisplayStyle.None;
            root.Add(_strip);
        }

        /// <summary>刷新 LLM 状态条。读的是真实配置，所以状态不会与实际行为脱节。</summary>
        void RefreshLlmStatus()
        {
            if (_llmStatus == null) return;

            var cfg = PerfAgentSettings.Config;

            // 配置窗口里改过「纯本地模式」时，这里的开关也要跟着走（同一个字段的两面）
            if (_aiToggle != null) _aiToggle.SetValueWithoutNotify(!cfg.localOnlyNoLlm);

            if (cfg.localOnlyNoLlm)
            {
                _llmStatus.text = "AI：已关闭（纯本地）";
                Theme.TintPill(_llmStatus, Theme.TextDim);
            }
            else if (!cfg.HasApiKey && !LlmClient.IsLocalEndpoint(cfg.endpoint))
            {
                _llmStatus.text = "AI：已启用 · 缺 Key（回答走规则引擎）";
                Theme.TintPill(_llmStatus, Theme.Warn);
            }
            else
            {
                string source = cfg.ApiKeySource;
                _llmStatus.text = "AI：" + cfg.model
                    + (string.IsNullOrEmpty(source) ? "" : "（Key 来源：" + source + "）");
                Theme.TintPill(_llmStatus, Theme.Good);
            }
        }

        /// <summary>
        /// 主面板的「启用 AI」开关。
        ///
        /// 改的就是配置里的 `localOnlyNoLlm` —— 与配置窗口同一个字段，所以两边永远是同一个状态，
        /// 不会出现「面板说开了、配置窗口说是纯本地」。打开时顺手校验一次连通性：
        /// 否则用户开了开关却发现回答还是规则引擎，会以为坏了（其实只是 Key / 网络不通）。
        /// </summary>
        void SetAiEnabled(bool on)
        {
            var cfg = PerfAgentSettings.Config;
            cfg.localOnlyNoLlm = !on;
            cfg.Save();
            PerfAgentSettingsWindow.NotifyExternalChange();
            RefreshLlmStatus();

            if (!on)
            {
                SetStatus("已关闭 AI：纯本地模式，不会调用任何外部服务；规则引擎的结论照常产出。");
                return;
            }

            if (!cfg.HasApiKey && !LlmClient.IsLocalEndpoint(cfg.endpoint))
            {
                SetStatus("AI 已启用，但还没配置 API Key —— 追问仍会走本地规则引擎。点右上「LLM 配置」填 Key，或先做一次连通性校验。");
                return;
            }

            SetStatus("AI 已启用，正在校验连通性（只发一个 \"ping\"，不带任何工程数据）…");
            LlmClient.PingEx(delegate (LlmClient.PingResult r)
            {
                SetStatus(r.ok
                    ? ("AI 已启用 · 连通正常（" + r.elapsedMs.ToString("0") + " ms"
                       + (string.IsNullOrEmpty(r.serverModel) ? "" : "，模型 " + r.serverModel) + "）")
                    : ("AI 已启用，但连通校验失败（" + r.errorKind + "）：点「LLM 配置」看逐项结果。"));
            });
        }

        /// <summary>
        /// 跟随采集的状态栏。
        ///
        /// 放在 EditorApplication.update 上是因为采集期间要实时显示已记录帧数；
        /// 限流与「帧数没变就不刷」见 _nextFollowPoll 的注释（刷界面本身会产生分配）。
        /// </summary>
        void PollFollowCapture()
        {
            // 尺寸自愈放在最前面：**不能只靠 OnEnable 与细条展开这两条路径**。
            // 实测反馈：「现在还是这样，往下拉一点啊」—— 面板被存成「宽而扁」（1460x85）后，
            // OnEnable 早已跑过、也没再经过细条切换，于是它就那么扁着；
            // 而宽度很大时旧的判定（看宽度就以为不是细条）还会把它当成「面板尺寸」存下来，越存越扁。
            // 这里按 1 秒限流主动校验一次：小到不可用就擑回来（细条期间跳过，停靠窗口 Unity 会忽略 position）。
            EnsureUsableWindowSizeThrottled();

            if (FollowCapture.Capturing)
            {
                // 采集一开始就把面板收成细条：面板常常就飘在 Game 视图旁边，
                // 整块面板会把画面挡掉一半。只自动收一次 —— 用户手动展开后就不再干涉。
                if (!_stripDoneForThisCapture)
                {
                    _stripDoneForThisCapture = true;
                    _warnedZeroFramesLive = false;   // 新一轮采集重新允许报一次「没采到帧」
                    _stripZeroSince = 0;
                    SetStripped(true, true);
                }

                double now = EditorApplication.timeSinceStartup;
                if (now < _nextFollowPoll) return;

                // 固定 4 Hz 刷新（不跟帧数变化挂钩）：帧率平稳时也要往前走，否则曲线会「卡住」看不出正在监视
                _nextFollowPoll = now + CaptureLiveStats.SampleIntervalSeconds;

                int frames = FollowCapture.CapturedFrames;
                if (!_liveActive)
                {
                    // 采集刚开：把波形起点对齐到这次采集的起点（面板帧号 − 已记录帧数）
                    _liveActive = true;
                    _live.Begin(ProfilerApi.LastFrameIndex - frames, now);
                }

                _live.Sample(ProfilerApi.LastFrameIndex, now);
                RefreshLiveStrip(true);
                RefreshStripText();      // 细条上的帧率/帧耗时（同一份样本，口径一致）

                if (frames != _lastFollowFrames)
                {
                    _lastFollowFrames = frames;
                    string summary = _live.Summary(true);
                    SetStatus(summary.Length > 0
                        ? summary
                        : ("跟随采集中（你自己操作）："
                           + CaptureWindow.CountLabel(frames, FollowCapture.RetainedFrames) + "。"));
                }
                return;
            }

            if (_liveActive)
            {
                // 从「采集中」变为「已结束」：冻结波形（保留最后一段曲线），并补一次终态提示
                _liveActive = false;
                _lastFollowFrames = -1;
                _stripDoneForThisCapture = false;
                // 结束了就自动展开 —— 收起期间屏幕上只有一条帧率，结论得让用户看得见。
                if (_stripped) SetStripped(false, true);
                RefreshLiveStrip(false);

                SetStatus(string.IsNullOrEmpty(FollowCapture.LastSnapshotId)
                    ? "跟随采集已结束（本次没有生成快照）"
                    : ("跟随采集已结束，快照 " + FollowCapture.LastSnapshotId
                       + " 已生成 —— 结论在「结论」标签，要贴给别人就点「复制结论」。"));
                return;
            }

            if (FollowCapture.Armed)
            {
                if (_nextFollowPoll <= 0)   // OnFollowCaptureChanged 会把它清零 → 只在状态变化时刷一次
                {
                    _nextFollowPoll = 1;
                    SetStatus("跟随采集已待命：正在量编辑器开销基线，随后会自动进入 Play 开始记录。点「跟随采集」可取消。");
                }
                return;
            }

            if (!string.IsNullOrEmpty(FollowCapture.LastSnapshotId) && _nextFollowPoll <= 0)
            {
                _nextFollowPoll = 1;
                SetStatus("跟随采集已结束，快照 " + FollowCapture.LastSnapshotId + " 已生成。");
            }
        }

        // =====================================================================
        // 实时帧率波形
        //
        // 目的：按下「跟随采集」后，帧率在跳这件事必须在面板里看得见 —— 而不是等采集结束
        // 才从报告里读一个 P95。图的纵轴上界至少 33 ms（30 FPS），否则一个平稳的 60 FPS
        // 过程会被画成满格噪点，看不出任何波动。
        //
        // 性能代价：每 0.25 秒写 120 个元素的 style。之所以能接受是因为采集期间
        // （也就是被测期间）这个频率下算下来是每帧几十字节的分配，远低于编辑器基线；
        // 也正因为如此，刷新必须限流（见 _nextFollowPoll 的注释）。
        // =====================================================================
        VisualElement BuildLiveStrip()
        {
            var host = new VisualElement();
            host.style.marginTop = 6;
            Theme.Pad(host, 8, 8, 6, 6);
            host.style.backgroundColor = Theme.CardBg;
            Theme.Rounded(host, Theme.Radius);
            Theme.Border1(host, Theme.Border);

            _liveLabel = new Label("");
            _liveLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _liveLabel.style.fontSize = Theme.SizeSmall;
            _liveLabel.style.color = Theme.Text;
            _liveLabel.style.whiteSpace = WhiteSpace.Normal;
            host.Add(_liveLabel);

            var bars = new VisualElement();
            bars.style.flexDirection = FlexDirection.Row;
            bars.style.alignItems = Align.FlexEnd;
            bars.style.height = 46;
            bars.style.marginTop = 3;
            host.Add(bars);

            _liveBars = new VisualElement[CaptureLiveStats.Capacity];
            for (int i = 0; i < _liveBars.Length; i++)
            {
                var bar = new VisualElement();
                bar.style.flexGrow = 1;
                bar.style.flexBasis = 0;
                bar.style.marginRight = 1;
                bar.style.height = 1;
                bar.style.backgroundColor = new Color(0.30f, 0.32f, 0.36f);
                bars.Add(bar);
                _liveBars[i] = bar;
            }
            return host;
        }

        void RefreshLiveStrip(bool capturing)
        {
            if (_liveHost == null || _liveBars == null) return;

            var w = _live.waveform;
            if (w.Count == 0)
            {
                _liveHost.style.display = DisplayStyle.None;
                return;
            }

            _liveHost.style.display = DisplayStyle.Flex;
            _liveLabel.text = _live.Summary(capturing);

            double ceiling = w.ChartCeilingMs();
            int offset = _liveBars.Length - w.Count;   // 样本不足时靠右对齐（最新的在最右）

            for (int i = 0; i < _liveBars.Length; i++)
            {
                var bar = _liveBars[i];
                int src = i - offset;
                if (src < 0)
                {
                    bar.style.height = 1;
                    bar.style.backgroundColor = new Color(0.30f, 0.32f, 0.36f);
                    continue;
                }

                double ms = w[src];
                float pct = Mathf.Clamp01((float)(ms / ceiling));
                bar.style.height = Length.Percent(Mathf.Max(2f, pct * 100f));
                bar.style.backgroundColor = BarColor(ms);
            }
        }

        /// <summary>单帧耗时 → 颜色：≥33 ms（<30 FPS）红，≥16.7 ms（<60 FPS）黄，其余绿。</summary>
        static Color BarColor(double ms)
        {
            if (ms >= 33.0) return Theme.Bad;
            if (ms >= 16.7) return Theme.Warn;
            return Theme.Good;
        }

        // =====================================================================
        // 细条模式（采集时别遮挡视线）
        // =====================================================================

        /// <summary>
        /// 采集期间的细条：只显示帧率/帧耗时/已记录帧数，加两个按钮。
        ///
        /// 不显示整个波形也不显示结论 —— 这个条的存在意义就是「不挡画面但知道在录」。
        /// 数字口径与面板上的实时波形完全一致（同一个 CaptureLiveStats）。
        /// </summary>
        VisualElement BuildStrip()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.backgroundColor = Theme.CardBg;
            Theme.Rounded(bar, Theme.Radius);
            Theme.Border1(bar, Theme.Border);
            Theme.Pad(bar, 8, 8, 2, 2);
            // 细条只有一行高：禁掉换行 + 裁掉溢出。
            // 实测踩过：文字换行后超出窗口高度，而 UI Toolkit 父元素默认不裁剪 ——
            // 多出来的那几行直接画在窗口外/上面，看起来就是「字叠在一起」。
            bar.style.overflow = Overflow.Hidden;

            // 胶囊也不参与压缩：窄的时候该省略的是文字。
            var pill = Theme.Pill("● 跟随采集中", Theme.Accent);
            pill.style.flexShrink = 0f;
            bar.Add(pill);

            _stripLabel = new Label("等 Profiler 出数…");
            _stripLabel.style.fontSize = Theme.SizeSmall;
            _stripLabel.style.color = Theme.Text;
            _stripLabel.style.marginLeft = 10;
            _stripLabel.style.marginRight = 10;
            _stripLabel.style.flexGrow = 1;
            _stripLabel.style.flexShrink = 1;
            _stripLabel.style.minWidth = 0;                     // 允许被压到比内容更窄（否则它撑开整行）
            _stripLabel.style.whiteSpace = WhiteSpace.NoWrap;   // 只允许一行（诊断详情走 tooltip / Console）
            // **标签自己也要 overflow: Hidden**：实测踩过 —— 只给外层条设 overflow 不够，
            // 文字会画出标签的矩形、直接叠在右边的按钮上（截图里就是「字叠在一起」）。
            // NoWrap + Hidden + Ellipsis 三件套才能把长文案改成末尾省略号。
            _stripLabel.style.overflow = Overflow.Hidden;
            _stripLabel.style.textOverflow = TextOverflow.Ellipsis;
            bar.Add(_stripLabel);

            // 展开/收起对用户是手动挡；自动收起只在采集开始时做一次。
            // 按钮不参与压缩（flexShrink=0）：窄的时候该省略的是文字，不是把按钮压没了。
            var expandBtn = Theme.Ghost("展开面板", delegate { SetStripped(false, true); });
            expandBtn.style.flexShrink = 0f;
            bar.Add(expandBtn);
            var stopBtn = Theme.Ghost("停止采集", ToggleFollowCapture);
            stopBtn.style.flexShrink = 0f;
            bar.Add(stopBtn);
            return bar;
        }

        /// <summary>明细区/对话区等全部子元素与细条二选一显示；顺便把窗口尺寸也收/放。</summary>
        void SetStripped(bool on, bool moveWindow)
        {
            if (_strip == null || _stripped == on) return;
            _stripped = on;

            var host = rootVisualElement;
            for (int i = 0; i < host.childCount; i++)
            {
                var child = host[i];
                if (child == _strip) continue;
                child.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _strip.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

            if (on)
            {
                // 进 Play 会触发域重载，字段会被清空，但**窗口尺寸是持久的** ——
                // 重载后重新收起时，当前尺寸可能已经是细条了，绝不能把它当成「原来的尺寸」存下来，
                // 否则采集结束后会「恢复」成一条细条、面板再也用不了。
                //
                // 判定必须用 NeedsGrow（要求**宽高都**达到面板下限），不能用「宽度够大就算面板」：
                // 「宽而扁」（实测 1460x85）会通过只看宽度的旧判定被存成「面板尺寸」，
                // 展开后就把面板恢复成一条扁窗口。
                if (!StripGeometry.NeedsGrow(position.width, position.height))
                {
                    _preStripRect = position;
                    SavePreStripRect(_preStripRect);
                }
                else if (!TryLoadPreStripRect(out _preStripRect))
                {
                    _preStripRect = new Rect(position.x, position.y,
                        StripGeometry.PanelWidth, StripGeometry.PanelHeight);
                }

                _preStripMinSize = minSize;
                // 根容器原本有 10/10/8/8 的内边距，在 40 多像素高的窗口里会直接把内容挤到换行溢出 ——
                // 细条期间把它压到 4，展开时再还原。
                _preStripPadding = new Vector4(rootVisualElement.style.paddingLeft.value.value,
                    rootVisualElement.style.paddingRight.value.value,
                    rootVisualElement.style.paddingTop.value.value,
                    rootVisualElement.style.paddingBottom.value.value);
                Theme.Pad(rootVisualElement, 4, 4, 4, 4);
                // minSize 是浮动窗口的硬约束：不改小的话窗口会被拉回 900x600，细条变成一张大空白面板。
                minSize = new Vector2(360, 24);
                if (moveWindow) ApplyStripRect();
            }
            else
            {
                // 展开：minSize 先恢复成面板下限。
                // 不能只信 _preStripMinSize —— 进 Play 会域重载，它早就被清掉了（实测就是这个原因
                // 让面板带着细条的 minSize 回来，于是能一直小下去）。
                minSize = _preStripMinSize.x > 1f
                    ? _preStripMinSize
                    : new Vector2(StripGeometry.PanelWidth, StripGeometry.PanelHeight);
                Theme.Pad(rootVisualElement, _preStripPadding.x, _preStripPadding.y, _preStripPadding.z, _preStripPadding.w);
                if (_preStripRect.width <= 1f && !TryLoadPreStripRect(out _preStripRect))
                    _preStripRect = new Rect(position.x, position.y,
                        StripGeometry.PanelWidth, StripGeometry.PanelHeight);
                if (moveWindow) RestoreStripRect();
                try { SessionState.EraseString(StripPrevRectKey); } catch { }
                // 恢复回来的 rect 也可能本身就被存小了 —— 兜底撑到可用尺寸。
                EnsureUsableWindowSize();
            }

            RefreshStripText();
        }

        void ApplyStripRect()
        {
            try
            {
                float x = _preStripRect.x + Mathf.Max(0f, (_preStripRect.width - StripGeometry.Width) * 0.5f);
                position = new Rect(x, _preStripRect.y, StripGeometry.Width, StripGeometry.Height);

                // 停靠中的窗口写 position 不生效（Unity 用布局管尺寸）—— 读回来对不上就当它停靠着，
                // 细条上写明「拖成浮动窗口才能真缩小」，而不是默默什么都不发生。
                _positionWritable = Mathf.Abs(position.width - StripGeometry.Width) < 1f
                                    && Mathf.Abs(position.height - StripGeometry.Height) < 1f;
            }
            catch { _positionWritable = false; }
        }

        void RestoreStripRect()
        {
            try
            {
                if (_preStripRect.width > 1f && _preStripRect.height > 1f) position = _preStripRect;
            }
            catch { }
        }

        /// <summary>
        /// 收起前的窗口尺寸存进 SessionState：进 Play 一定会触发域重载，
        /// 光靠字段的话「恢复原来的尺寸」会退化成恢复成一个 0x0。
        /// </summary>
        const string StripPrevRectKey = "PerfAgent.UI.PreStripRect";

        static void SavePreStripRect(Rect r)
        {
            try
            {
                SessionState.SetString(StripPrevRectKey, StripGeometry.Format(r.x, r.y, r.width, r.height));
            }
            catch { }
        }

        static bool TryLoadPreStripRect(out Rect r)
        {
            r = new Rect();
            try
            {
                float x, y, w, h;
                if (!StripGeometry.TryParse(SessionState.GetString(StripPrevRectKey, ""), out x, out y, out w, out h))
                    return false;
                r = new Rect(x, y, w, h);
                return true;
            }
            catch { return false; }
        }

        /// <summary>刷新细条上的数字（跟面板上的实时波形同一个口径）。</summary>
        void RefreshStripText()
        {
            if (_stripLabel == null) return;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var w = _live.waveform;
            string text;
            if (w.Count == 0)
            {
                text = "等 Profiler 出数…（" + CaptureWindow.CountLabelShort(FollowCapture.CapturedFrames, FollowCapture.RetainedFrames) + "）";
            }
            else
            {
                text = w.Fps().ToString("0.#", culture) + " FPS"
                     + "　帧耗时 P50 " + w.P50Ms().ToString("0.##", culture)
                     + " / P95 " + w.P95Ms().ToString("0.##", culture)
                     + " / 峰值 " + w.MaxMs().ToString("0.##", culture) + " ms"
                     + "　" + CaptureWindow.CountLabelShort(FollowCapture.CapturedFrames, FollowCapture.RetainedFrames);
            }
            if (!_positionWritable)
                text += "　·　停靠中：拖成浮动窗口才能缩小";

            // 「持续 0 帧」是最难自查的一种状态：采集在跑、数字不动，用户分不清是工具坏了还是 Profiler 没录。
            // 细条只有一行，所以这里只放一个短标记 —— 详细原因 + 自愈动作走 Console（见 WarnNoFramesOnce）。
            if (FollowCapture.Capturing && FollowCapture.CapturedFrames == 0)
            {
                double now = EditorApplication.timeSinceStartup;
                if (_stripZeroSince <= 0) _stripZeroSince = now;
                if (now - _stripZeroSince > 2.0)
                {
                    text += "　·　长时间 0 帧";   // 详情在 tooltip / Console：细条只有一行，长句会被裁成省略号
                    WarnNoFramesOnce(now - _stripZeroSince);
                }
            }
            else
            {
                _stripZeroSince = 0;
            }

            if (!string.Equals(_stripLabel.text, text, StringComparison.Ordinal))
            {
                _stripLabel.text = text;
                // 口径与 Profiler 完整状态都放 tooltip：细条上只放数字与一句原因。
                _stripLabel.tooltip = "口径：" + _live.Source
                    + "\n采集期间只留这一条，结束后面板会自动展开。"
                    + (FollowCapture.Capturing && FollowCapture.CapturedFrames == 0
                        ? "\n" + PanelCapture.DescribeProfilerState() : "");
            }
        }

        /// <summary>「采集中但一帧都没录到」的一句话原因（也进 Console）。</summary>
        static string NoFrameReason()
        {
            // 用 EnabledRaw：Enabled 的 getter 会拿 profileEditor 顶，
            // 会把「profileEditor 开着、enabled 关着」误报成「在记录」（实测把排查带偏过）。
            if (!ProfilerApi.EnabledRaw)
                return "Profiler 没在记录（ProfilerDriver.enabled=false）：到 Profiler 窗口点一下 Record";
            // 2026-10-07 现场就是这一支（enabled=true、profileEditor=false、first/last 均 -1）。
            // 它**不等于**已证实的原因（采集层会用记录目标阶梯实测三种组合），但先说清「目标看着不对」
            // 比笼统说一句「在记录却没写出帧」有用。
            if (!ProfilerApi.ProfileEditor)
                return "Profiler 的记录目标不是编辑器（profileEditor=false）：enabled 开着也可能一帧都不写，正在实测三种组合";
            if (ProfilerApi.LastFrameIndex < 0)
                return "两个开关都是开的，但一帧都没写出来（lastFrameIndex=-1）：看是不是 Profiler 窗口在暂停，或系统内存告警导致 Unity 自己丢帧";
            return "Profiler 有帧（last=" + ProfilerApi.LastFrameIndex + "）但本次起点对不上，已自动重定 —— 再等一帧";
        }

        /// <summary>
        /// 采集期间长时间 0 帧：报一次详细诊断（**不再自己动手救** —— 自救是采集层的
        /// 记录目标阶梯干的，见 <c>PanelCapture.RunTargetLadder</c>，它会按顺序实测并写出结论）。
        ///
        /// 这里只做窗口这一侧该做的事：把用户看得见的细条状态与 Profiler 真值对上号，
        /// 并且**只报一次** —— 采集期间刷日志本身会产生分配，会污染测量。
        /// </summary>
        void WarnNoFramesOnce(double zeroSeconds)
        {
            if (_warnedZeroFramesLive) return;
            _warnedZeroFramesLive = true;

            Debug.LogWarning("[PerfAgent] 采集已开始 " + zeroSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                + " 秒，但一帧都没采到：" + NoFrameReason()
                + "\n" + PanelCapture.DescribeProfilerState()
                + "\n自救：采集层会在面板一帧不写时按顺序实测三种记录目标组合，"
                + "并把「哪一步开始出帧 / 全都无效」写进日志与快照备注 —— 请往下翻看 "
                + "[PerfAgent] 开头的那几条日志。"
                + "\n若已经报「三种组合都无效」：① 看系统内存是否告急（Unity 会报 running out of memory，此时它自己丢帧）；"
                + "② 打开 Profiler 窗口确认 Record（红点）亮着、窗口没被暂停；"
                + "③ 菜单 Tools/PerfAgent/API 探针 → 把【1】与【1b】两段发出来。");
        }


        VisualElement BuildToolbar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.flexWrap = Wrap.Wrap;
            bar.style.alignItems = Align.Center;

            // 主操作单独一组：整屏只有一个强调色按钮
            bar.Add(Theme.Primary("跟随采集", ToggleFollowCapture));
            bar.Add(Theme.Secondary("复制结论", CopyReport));
            bar.Add(Theme.Secondary("复制修复建议", CopyFixSuggestions));
            bar.Add(Theme.Divider(true));
            bar.Add(Theme.Secondary("静态审计", RunStaticAudit));
            bar.Add(Theme.Secondary("重新分析", delegate
            {
                if (PerfSession.Current == null) { SetStatus("没有快照"); return; }
                PerfPipeline.Analyze(PerfSession.Current);
                PerfPipeline.SaveAndSetCurrent(PerfSession.Current);
                SetStatus("已重新分析");
            }));
            bar.Add(Theme.Divider(true));
            bar.Add(Theme.Secondary("导出 MD", delegate { Export(false); }));
            bar.Add(Theme.Secondary("导出 HTML", delegate { Export(true); }));
            bar.Add(Theme.Divider(true));
            bar.Add(Theme.Ghost("新会话", NewConversation));
            bar.Add(Theme.Ghost("收起为细条", delegate { SetStripped(true, true); }));
            return bar;
        }

        void BuildTabs()
        {
            _tabRow.Clear();
            AddTab("findings", "结论");
            AddTab("metrics", "指标");
            AddTab("frames", "帧");
            AddTab("markers", "Marker");
            AddTab("assets", "资源");
            AddTab("scene", "场景");
            AddTab("code", "代码");
            AddTab("diff", "对比");
            AddTab("fixlog", "变更");
            AddTab("notes", "提示");
        }

        void AddTab(string id, string label)
        {
            _tabRow.Add(Theme.Tab(label, _activeTab == id, delegate
            {
                _activeTab = id;
                RefreshDetails();
            }));
        }

        VisualElement BuildBudgetEditor()
        {
            var box = new VisualElement();
            var b = PerfAgentSettings.Config.budget;

            box.Add(Theme.GroupLabel("目标与帧"));
            box.Add(BudgetField("目标帧率 (fps)", b.targetFrameRate, delegate (double v) { b.targetFrameRate = (float)v; Apply(); }));
            box.Add(BudgetField("每帧分配 (B)", b.maxManagedAllocBytesPerFrame, delegate (double v) { b.maxManagedAllocBytesPerFrame = (long)v; Apply(); }));

            box.Add(Theme.GroupLabel("渲染"));
            box.Add(BudgetField("Draw Call", b.maxDrawCalls, delegate (double v) { b.maxDrawCalls = (int)v; Apply(); }));
            box.Add(BudgetField("SetPass Call", b.maxSetPassCalls, delegate (double v) { b.maxSetPassCalls = (int)v; Apply(); }));
            box.Add(BudgetField("三角面", b.maxTriangles, delegate (double v) { b.maxTriangles = (long)v; Apply(); }));

            box.Add(Theme.GroupLabel("内存"));
            box.Add(BudgetField("纹理内存 (MB)", b.maxTextureMemoryMB, delegate (double v) { b.maxTextureMemoryMB = (long)v; Apply(); }));
            box.Add(BudgetField("TempAlloc (MB)", b.maxTempAllocatorMB, delegate (double v) { b.maxTempAllocatorMB = (long)v; Apply(); }));

            var foot = new VisualElement();
            foot.style.flexDirection = FlexDirection.Row;
            foot.style.marginTop = 6;
            foot.Add(Theme.Ghost("恢复默认预算", ResetBudget));
            box.Add(foot);

            box.Add(Theme.Hint("这些阈值是诊断的判定标准；改动会立刻重算当前快照。更完整的内存/阴影/音频预算在 Project Settings > PerfAgent 里。"));
            return box;
        }

        /// <summary>恢复默认预算（按钮点完先重建表单，否则输入框里还是旧值）。</summary>
        void ResetBudget()
        {
            var b = PerfAgentSettings.Config.budget;
            var d = new PerfBudget();
            b.targetFrameRate = d.targetFrameRate;
            b.maxManagedAllocBytesPerFrame = d.maxManagedAllocBytesPerFrame;
            b.maxDrawCalls = d.maxDrawCalls;
            b.maxSetPassCalls = d.maxSetPassCalls;
            b.maxTriangles = d.maxTriangles;
            b.maxTextureMemoryMB = d.maxTextureMemoryMB;
            b.maxTempAllocatorMB = d.maxTempAllocatorMB;

            Apply();
            if (_budgetHost != null)
            {
                _budgetHost.Clear();
                _budgetHost.Add(BuildBudgetEditor());
            }
            SetStatus("预算已恢复默认值");
        }

        static VisualElement BudgetField(string label, double value, Action<double> onChange)
        {
            var field = new DoubleField();
            field.value = value;
            field.style.fontSize = Theme.SizeSmall;
            field.RegisterValueChangedCallback(delegate (ChangeEvent<double> e)
            {
                if (Math.Abs(e.newValue - e.previousValue) < 1e-9) return;
                onChange(e.newValue);
            });
            return Theme.FormRow(label, field);
        }

        void Apply()
        {
            PerfAgentSettings.Config.Save();
            if (PerfSession.Current != null)
            {
                PerfPipeline.Analyze(PerfSession.Current);
                RefreshDetails();
            }
            SetStatus("预算已更新");
        }

        VisualElement BuildChatPanel()
        {
            var card = Theme.Card("对话追问", "本地先给结论；开 AI 再多一段解释");
            card.tooltip = "本地规则引擎只按关键词找维度（帧率 / 渲染 / 内存 / 资源 / 代码 / 物理 / 场景 / 采集），"
                         + "不理解句子意思、也不联网；开 AI 后，它会在本地结论之上补上因果、取舍与具体改法。";
            card.style.marginTop = 6;
            // 与明细区一起瓜分右栏高度：窗口高的时候记录区跟着变高，
            // 而不是永远卡在一个 150 px 的小窗里（那样长回答根本没法读）。
            card.style.flexGrow = 3;
            card.style.minHeight = 200;

            // 标题行右侧放按钮：长在标题里就不占额外的高度
            var titleRow = Theme.CardTitleRow(card);
            if (titleRow != null)
            {
                var grow = new VisualElement();
                grow.style.flexGrow = 1;
                titleRow.Add(grow);

                var copyChat = Theme.Ghost("复制对话", CopyConversation);
                copyChat.tooltip = "把整个对话（含本地结论与 AI 回答）复制成 Markdown";
                titleRow.Add(copyChat);
                titleRow.Add(Theme.Ghost("新会话", NewConversation));
            }

            // 记录区：外层容器负责「占多高」，ScrollView 绝对填充它。
            //
            // 为什么不能直接把 flexGrow 加在 ScrollView 上（踩过）：
            // ScrollView 的基准高度 = 内容高度，而对话卡又是 flexShrink=0，
            // 两者一叠，卡片就被内容撑破 —— 结果是记录区自己不需要滚动（没滚动条），
            // 多出来的内容直接超出窗口被裁掉。用绝对定位就不会把内容高度算进基准。
            var transcriptBox = new VisualElement();
            transcriptBox.style.flexGrow = 1;
            transcriptBox.style.minHeight = 84;
            card.Add(transcriptBox);

            _transcriptScroll = new ScrollView(ScrollViewMode.Vertical);
            _transcriptScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _transcriptScroll.style.position = Position.Absolute;
            _transcriptScroll.style.left = 0;
            _transcriptScroll.style.top = 0;
            _transcriptScroll.style.right = 0;
            _transcriptScroll.style.bottom = 0;
            _transcriptScroll.style.backgroundColor = Theme.SunkenBg;
            Theme.Rounded(_transcriptScroll, 4f);
            Theme.Border1(_transcriptScroll, Theme.Border);
            Theme.Pad(_transcriptScroll, 8, 8, 6, 6);
            transcriptBox.Add(_transcriptScroll);

            _transcript = new Label();
            _transcript.style.whiteSpace = WhiteSpace.Normal;
            _transcript.style.fontSize = Theme.SizeBody;
            _transcript.style.color = Theme.Text;
            // 段落之间本来就靠空行分隔（AppendTranscript 里写的），
            // 再加一点下边距，扫读时不会觉得整块文字挤在一起
            _transcript.style.marginBottom = 8;
            _transcriptScroll.Add(_transcript);

            // 示例问题：单行横向滚动。
            //
            // 以前是换行排的，四条样例吃掉三行高度 —— 在一个本来就紧的底部区域里，
            // 那三行比示例本身值钱。改单行 + 收起滚动条。
            //
            // 「需 AI」的标注仍然有用：让用户**在提问前**就看出两条路的分工。
            var samples = new ScrollView(ScrollViewMode.Horizontal);
            samples.style.height = 26;
            samples.style.marginTop = 4;
            samples.style.flexShrink = 0;
            samples.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            var samplesLine = samples.contentContainer;
            samplesLine.style.flexDirection = FlexDirection.Row;
            samplesLine.style.alignItems = Align.Center;
            SampleChip(samplesLine, "帧耗时超预算了吗？", false);
            SampleChip(samplesLine, "每帧分配从哪来？", false);
            SampleChip(samplesLine, "为什么只有战斗时才卡？", true);
            SampleChip(samplesLine, "我该先修哪一个？", true);
            card.Add(samples);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 5;
            row.style.flexShrink = 0;
            // 空间不够时宁可让按钮换到下一行，也不能把它挤出可视区
            //（实测：输入框 flexGrow 把按钮顶到了卡片外，看起来就像「没有发送按钮」）
            row.style.flexWrap = Wrap.Wrap;
            card.Add(row);

            _input = new TextField();
            _input.multiline = true;
            _input.style.flexGrow = 1;
            _input.style.flexShrink = 1;
            _input.style.minWidth = 120;     // 可收缩，但至少留得下几个字
            _input.style.height = 46;
            _input.style.fontSize = Theme.SizeBody;
            _input.RegisterCallback<KeyDownEvent>(delegate (KeyDownEvent evt)
            {
                if ((evt.ctrlKey || evt.commandKey) && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter))
                {
                    Send();
                    evt.StopPropagation();
                }
            });
            row.Add(_input);

            _sendButton = Theme.Primary("发送", Send);
            _sendButton.style.width = 92;
            // flexShrink 默认是 1：不给 minWidth 的话这个按钮会被攼到看不见
            _sendButton.style.minWidth = 92;
            _sendButton.style.flexShrink = 0;
            _sendButton.style.flexGrow = 0;
            _sendButton.style.height = 46;
            _sendButton.style.marginLeft = 6;
            _sendButton.style.marginRight = 0;
            _sendButton.style.marginBottom = 0;
            _sendButton.style.fontSize = Theme.SizeSmall;
            _sendButton.tooltip = "发送（Ctrl+Enter）";
            row.Add(_sendButton);

            var foot = new Label("Ctrl+Enter 发送。没配 LLM Key 也能用 —— 走本地规则引擎。");
            foot.style.fontSize = Theme.SizeSmall;
            foot.style.color = Theme.TextFaint;
            foot.style.marginTop = 3;
            foot.style.whiteSpace = WhiteSpace.Normal;
            card.Add(foot);

            return card;
        }

        static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = Theme.SizeSmall;
            label.style.marginTop = 8;
            label.style.marginBottom = 2;
            label.style.color = Theme.TextDim;
            return label;
        }

        // =====================================================================
        // 动作
        // =====================================================================

        void RunStaticAudit()
        {
            SetStatus("静态审计中…");
            PerfPipeline.StaticAudit(false, SetStatus);
            SetStatus("静态审计完成");
            RefreshSnapshots();
            RefreshDetails();
        }

        void Export(bool html)
        {
            if (PerfSession.Current == null) { SetStatus("没有快照可导出"); return; }
            string path = PerfReportExporter.Save(PerfSession.Current, html);
            SetStatus("已导出：" + path);
            EditorUtility.RevealInFinder(path);
        }

        /// <summary>
        /// 把当前快照的分析结果整份复制到剪贴板（Markdown）。
        ///
        /// 用户拿到结论后最常做的是「贴到别处」：发给同事、写进 issue、或者丢给外部 AI 继续追问，
        /// 要求他先去导出文件再打开复制是多此一举。复制的就是导出报告用的同一份 Markdown，
        /// 所以「看到的」与「导出的」永远一致。
        /// </summary>
        void CopyReport()
        {
            var snap = PerfSession.Current;
            if (snap == null)
            {
                AppendTranscript("\n⚠ **没有可复制的内容**：还没有快照 —— 先点「跟随采集」跑一段，"
                               + "或从左上列表载入历史快照。\n");
                SetStatus("没有快照可复制：先点「跟随采集」跑一段，或从左上列表载入历史快照。");
                return;
            }

            string md;
            try
            {
                md = PerfReportExporter.ToMarkdown(snap);
            }
            catch (Exception e)
            {
                // 报告生成抛异常时绝不能静默：用户点了复制、剪贴板却没变，只会以为工具坏了。
                Debug.LogWarning("[PerfAgent] 生成 Markdown 报告失败：" + e);
                AppendTranscript("\n⚠ **复制失败**：报告生成抛异常（" + e.GetType().Name + "）："
                               + e.Message + "。堆栈在 Console。\n");
                SetStatus("报告生成失败（" + e.GetType().Name + "）：" + e.Message + "。看 Console 里的堆栈。");
                return;
            }

            CopyToClipboard(md, "Markdown 报告（环境 / 指标 / 结论 / 修复计划）");
        }

        /// <summary>
        /// **所有「复制」都走这里**：先回读剪贴板确认真的写进去了，并且把结果同时写到
        /// 状态栏与对话区。
        ///
        /// 为什么非要回读、非要留一句话（实测反馈：「点「复制结论」没被复制」）：
        /// 复制本身是静默的 —— 内容为空、或生成报告抛异常时，用户看到的就是「什么都没发生」，
        /// 连该不该再点一次都不知道。现在无论成还是没成，都会有一行字说明原因与字数。
        /// </summary>
        void CopyToClipboard(string text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                string empty = CopyFeedback.Describe(what, 0, false);
                AppendTranscript("\n⚠ **" + empty + "**\n");
                SetStatus(empty);
                return;
            }

            try
            {
                EditorGUIUtility.systemCopyBuffer = text;

                // 写完立刻读回来对一下字符数：这是能确认「真的进剪贴板了」的唯一手段。
                bool verified = CopyFeedback.Verified(text, EditorGUIUtility.systemCopyBuffer);
                string message = CopyFeedback.Describe(what, text.Length, verified);

                AppendTranscript("\n**" + message + "** Ctrl+V 即可粘贴。\n");
                SetStatus(message);
            }
            catch (Exception e)
            {
                AppendTranscript("\n⚠ **复制失败**（" + what + "）：" + e.Message + "\n");
                SetStatus("复制失败（" + what + "）：" + e.Message);
            }
        }

        /// <summary>把整个对话（用户问题 + 本地结论 + AI 回答）复制成 Markdown。</summary>
        void CopyConversation()
        {
            string text = _transcriptRaw == null ? "" : _transcriptRaw.ToString();
            CopyToClipboard(text, "对话记录");
        }

        /// <summary>
        /// 「哪些代码需要修、怎么修」—— 本地版直接复制规则扫描的清单；开了 AI 则让 AI 在清单之上给出具体改法。
        ///
        /// 为什么不是一个按钮一种输出：这两份东西的性质不同 ——
        /// 本地清单是**扫描出来的事实**（文件:行 + 规则建议、可核对），
        /// AI 版是**建议**（含可替换的代码，可能出错、必须人看）。
        /// 所以状态栏会明确说这次给的是哪一种，AI 版还会记一条操作日志。
        /// </summary>
        void CopyFixSuggestions()
        {
            var snap = PerfSession.Current;
            if (snap == null) { SetStatus("没有快照：先跑一次采集或载入历史快照。"); return; }

            var cfg = PerfAgentSettings.Config;
            if (cfg.localOnlyNoLlm || !cfg.HasApiKey)
            {
                string local = FixSuggestionBrief.LocalChecklist(snap);
                CopyToClipboard(local, snap.codeIssues.Count == 0
                    ? "本地清单（扫描没发现代码反模式，附上了相关结论）"
                    : ("本地修复清单（" + snap.codeIssues.Count + " 处，来自规则扫描，不含具体改法）"));
                SetStatus(snap.codeIssues.Count == 0
                    ? "已复制本地清单（扫描没发现代码反模式，附上了相关结论）。"
                    : ("已复制本地修复清单（" + snap.codeIssues.Count + " 处，来自规则扫描，不含具体改法）。"
                       + (cfg.localOnlyNoLlm ? " 开 AI 后能给到「改成什么代码」。" : " 配好 Key 后能给到「改成什么代码」。")));
                return;
            }

            // 隐私开关要如实生效：关着「允许上传代码片段」时，只发文件:行与模式名，不发源码
            bool withCode = cfg.allowSourceCodeUpload;
            string brief = FixSuggestionBrief.Build(snap, withCode);

            AppendTranscript("\n**我**：请给出修复清单（" + snap.codeIssues.Count + " 处代码问题"
                           + (withCode ? "，含代码片段" : "，未上传代码片段") + "）\n\n");

            PerfHistory.RecordOperation("human", "ai", "fix_suggestions", "让 AI 生成修复清单",
                "", "", true, snap.codeIssues.Count,
                withCode ? "已发送代码片段" : "未发送代码片段（隐私开关关闭）", false);

            if (_sendButton != null) SetSending(true);
            SetStatus("正在让 AI 生成修复清单" + (withCode ? "…" : "（未上传代码片段，只发了文件:行）…"));

            int streamed = 0;
            Agent.Ask(FixSuggestionBrief.Prompt,
                delegate (string delta)
                {
                    streamed += delta.Length;
                    SetStatus("生成中… " + streamed + " 字");
                },
                delegate (string text)
                {
                    if (_sendButton != null) SetSending(false);
                    if (string.IsNullOrEmpty(text))
                    {
                        SetStatus("AI 没返回正文（看 Console 或 LLM 配置）。");
                        return;
                    }
                    // 不再把模型原文倒进对话区：它带 `#` 标题与 ``` 代码块，
                    // 而对话记录是一个 Label（只认 `**粗体**`），直接贴过来就是一堵原始 Markdown。
                    CopyToClipboard(text, "AI 修复清单（建议，不是审计结果）");
                    SetStatus("已复制 AI 修复清单（" + text.Length + " 字符）。先看一遍再改 —— 这是建议，不是审计结果。");
                    SaveConversation();
                },
                delegate (string error)
                {
                    if (_sendButton != null) SetSending(false);
                    AppendTranscript("\n⚠ " + error + "\n");
                    SetStatus("AI 生成失败：" + error);

                    // 这次没拿到正文，但这个按钮的意义就是「给我能贴走的东西」——
                    // 留一个空剪贴板是最差的结果（实测反馈：点完复制以为复制坏了）。
                    // 所以退回本地清单，并明说给的是本地事实、不是 AI 建议。
                    CopyToClipboard(FixSuggestionBrief.LocalChecklist(snap),
                        "AI 没给正文，改为复制本地清单（规则扫描的事实，不含具体改法）");
                },
                brief);
        }

        void Send()
        {
            if (_sending) return;

            var text = _input.value;
            if (string.IsNullOrEmpty(text)) return;

            _input.value = "";
            AppendTranscript("\n**我**：" + text + "\n\n");
            SetSending(true);

            var cfg = PerfAgentSettings.Config;
            var snap = PerfSession.Current;

            // 两条路都先给「本地规则引擎」的结论。
            //
            // 它不是降级品，而是这个工具的事实底座：数字与结论由规则算出、可逐条回溯，
            // AI 不应该重算它们。所以每次提问都把这一半固定贴出来 ——
            // 开了 AI 时用户就能当场看到两条路的差别（本地给事实，AI 在此基础上给因果与取舍），
            // 而不是靠文档解释「AI 有什么用」。
            AppendTranscript("**本地规则引擎**（规则算出来的结论与数字，可逐条回溯）\n\n"
                           + LocalAnswer.Answer(snap, text, true));

            if (cfg.localOnlyNoLlm || !cfg.HasApiKey)
            {
                SetSending(false);
                SetStatus(cfg.localOnlyNoLlm
                    ? "已用本地规则引擎回答（纯本地模式，未联网）"
                    : "已用本地规则引擎回答（未配置 LLM）");
                SaveConversation();
                return;
            }

            AppendTranscript("\n**AI 解释（" + cfg.model + "）**\n\n");

            var streamed = new StringBuilder();
            Agent.Ask(text,
                delegate (string delta)
                {
                    streamed.Append(delta);
                    SetStatus("生成中… " + streamed.Length + " 字");
                },
                delegate (string answer)
                {
                    AppendTranscript("\n" + answer + "\n");
                    SetSending(false);
                    SetStatus("完成");
                    SaveConversation();
                },
                delegate (string error)
                {
                    AppendTranscript("\n⚠ " + error + "\n");
                    SetSending(false);
                    SetStatus("失败");
                    SaveConversation();
                },
                // 把本地结论作为事实底稿一起发过去：AI 在确定性的那一半之上解释，
                // 数字不会被模型改写，也省掉一轮工具调用。
                LocalAnswer.BriefForPrompt(snap, text));
        }

        /// <summary>
        /// 示例问题标签：点一下把问题填进输入框（不直接发送）。
        /// needsAi = true 的会标上「需 AI」—— 本地引擎只比预算、不理解句子，
        /// 这类「为什么 / 先修哪个」的问题它只能给事实，得说在前面。
        /// </summary>
        void SampleChip(VisualElement host, string question, bool needsAi)
        {
            var b = Theme.Ghost(needsAi ? question + "（需 AI）" : question, delegate
            {
                if (_input == null) return;
                _input.value = question;
                _input.Focus();

                bool aiReady = !PerfAgentSettings.Config.localOnlyNoLlm && PerfAgentSettings.Config.HasApiKey;
                SetStatus(needsAi && !aiReady
                    ? "这是解释类问题（要因果与取舍），本地规则引擎答不了 —— 点右上「启用 AI」后再发（Ctrl+Enter）。"
                    : "已填入输入框，Ctrl+Enter 发送。");
            });
            b.style.fontSize = Theme.SizeSmall;
            b.style.marginRight = 4;
            b.style.marginBottom = 2;
            host.Add(b);
        }

        /// <summary>
        /// 发送中 / 空闲 的按钮状态。
        ///
        /// 不再只调 SetEnabled(false)：Unity 深色主题下被禁用的按钮会跟卡片底色几乎同色，
        /// 看起来就像「发送按钮不见了」（实测有人就是这么反馈的）。
        /// 所以这里自己改文字 + 配色：变灰但看得见，且状态一目了然。
        /// </summary>
        void SetSending(bool busy)
        {
            _sending = busy;
            if (_input != null) _input.SetEnabled(!busy);
            if (_sendButton == null) return;

            _sendButton.text = busy ? "生成中…" : "发送";
            _sendButton.SetEnabled(!busy);
            _sendButton.style.backgroundColor = busy ? Theme.BtnBg : Theme.Accent;
            _sendButton.style.color = busy ? Theme.TextDim : Color.white;
        }

        // =====================================================================
        // 会话持久化（P3）
        // =====================================================================

        /// <summary>把当前对话记录与 LLM 上下文落盘，堆在 ProjectSettings/PerfAgent/Conversations。</summary>
        void SaveConversation()
        {
            if (_transcript == null) return;
            if (string.IsNullOrEmpty(_conversationId))
                _conversationId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            var conversation = new PerfConversation();
            conversation.id = _conversationId;
            conversation.transcript = _transcript.text ?? "";
            conversation.messages = _agent == null ? new List<object>() : _agent.ExportMessages();
            PerfConversationStore.Save(conversation);
        }

        /// <summary>重开面板 / 域重载后恢复上次会话，包括可继续对话的上下文。</summary>
        void RestoreLatestConversation()
        {
            if (_transcript == null) return;
            var conversation = PerfConversationStore.LoadLatest();
            if (conversation == null) return;

            _conversationId = conversation.id;
            _transcript.text = conversation.transcript;
            Agent.RestoreMessages(conversation.messages);
            SetStatus("已恢复上次会话 " + conversation.id + "（上下文 " + Agent.MessageCount + " 条），可直接继续提问。");
        }

        void NewConversation()
        {
            SaveConversation();
            _conversationId = "";
            Agent.Reset();
            if (_transcript != null) _transcript.text = "";
            AppendTranscript("**新会话已开始**\n\n上一会话已保存到 ProjectSettings/PerfAgent/Conversations。\n");
            SetStatus("已开启新会话");
        }

        string SummarizeForChat(PerfSnapshot snap)
        {
            var sb = new StringBuilder();
            sb.Append("\n**采集完成**\n\n");
            sb.Append("| 指标 | 值 | 预算 |\n|---|---:|---:|\n");

            string[] keys = { "帧耗时均值", "帧耗时 P95", "帧耗时峰值", "实际 FPS", "每帧托管分配", "Draw Calls 峰值", "TempAllocator", "总分配内存" };
            for (int i = 0; i < keys.Length; i++)
            {
                var m = snap.FindMetric(keys[i]);
                if (m == null) continue;
                sb.Append("| ").Append(m.name).Append(" | ").Append(m.value.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(' ').Append(m.unit).Append(" | ")
                  .Append(string.IsNullOrEmpty(m.budget) ? "-" : m.budget + " " + m.budgetUnit).Append(" |\n");
            }

            int shown = 0;
            for (int i = 0; i < snap.findings.Count && shown < 6; i++)
            {
                var f = snap.findings[i];
                if (f.severity == Severity.Info) continue;
                sb.Append("\n- **").Append(f.severity == Severity.Error ? "严重" : "警告").Append("** ").Append(f.title)
                  .Append("\n  → ").Append(f.recommendation);
                shown++;
            }
            if (shown == 0) sb.Append("\n未发现超出预算的问题。");
            sb.Append('\n');
            return sb.ToString();
        }

        // =====================================================================
        // 刷新
        // =====================================================================

        void SetStatus(string text)
        {
            if (_status != null) _status.text = text;
        }

        void AppendTranscript(string markdown)
        {
            if (_transcript == null) return;

            // 保留原始 Markdown，渲染时再转富文本：
            // 直接对累加结果做转换，`**` 跨两次追加（流式回答）时才不会碎掉。
            _transcriptRaw.Append(markdown);
            _transcript.text = Theme.RichText(_transcriptRaw.ToString());

            // 滚到最后：直接设一次会被裁掉（此时内容高度还是旧的，offset 被夹到 0，
            // 表现就是“追加了长回答却停在开头”），所以下一个 panel tick 再设一次。
            ScrollTranscriptToEnd();
            _transcriptScroll.schedule.Execute(ScrollTranscriptToEnd);
        }

        void ScrollTranscriptToEnd()
        {
            if (_transcriptScroll == null) return;
            _transcriptScroll.scrollOffset = new Vector2(0, float.MaxValue);
        }

        void RefreshSnapshots()
        {
            if (_snapshotHost == null) return;
            _snapshotHost.Clear();

            var paths = PerfSnapshotStore.List();

            // 头部：数量 + 一键清空。
            // 快照会一直堆在 ProjectSettings/PerfAgent/Snapshots 下（虽然已按保留个数自动清，
            // 但用户想要「现在就把它们全删掉」时得有个手动的口子）。
            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;

            var count = Theme.Pill("共 " + paths.Count + " 个", Theme.TextDim);
            count.style.flexGrow = 0;
            head.Add(count);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            head.Add(spacer);

            var clear = Theme.Ghost("清空全部", delegate
            {
                if (paths.Count == 0) { SetStatus("没有可删除的快照。"); return; }
                if (!EditorUtility.DisplayDialog("PerfAgent",
                        "删除全部 " + paths.Count + " 个快照？此操作不可撤销。", "删除", "取消")) return;

                int n = PerfSnapshotStore.DeleteAll();
                PerfSession.SetCurrent(null, "");
                RefreshSnapshots();
                RefreshDetails();
                SetStatus("已删除 " + n + " 个快照。");
            });
            head.Add(clear);
            _snapshotHost.Add(head);

            if (paths.Count == 0)
            {
                _snapshotHost.Add(Theme.Hint("暂无快照：点上面「跟随采集」跑一段就有了。"));
                return;
            }

            for (int i = 0; i < paths.Count && i < 30; i++)
            {
                string path = paths[i];
                string id = System.IO.Path.GetFileNameWithoutExtension(path);
                bool isCurrent = PerfSession.CurrentPath == path;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;

                var button = new Button(delegate
                {
                    var snap = PerfSnapshotStore.Load(path);
                    if (snap == null) { SetStatus("读取失败"); return; }
                    PerfPipeline.Analyze(snap);
                    PerfSession.SetCurrent(snap, path);
                    SetStatus("已加载 " + id);
                });
                button.text = (isCurrent ? "▶ " : "　") + id;
                button.style.flexGrow = 1;
                button.style.flexShrink = 1;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.fontSize = Theme.SizeSmall;
                button.style.marginRight = 2;
                button.style.marginBottom = 2;
                button.style.paddingLeft = 6;
                Theme.Rounded(button, 4f);
                if (isCurrent) button.style.color = new Color(0.6f, 0.85f, 1f);
                row.Add(button);

                var del = new Button(delegate
                {
                    if (!EditorUtility.DisplayDialog("PerfAgent", "删除快照 " + id + "？", "删除", "取消")) return;
                    if (!PerfSnapshotStore.Delete(path)) { SetStatus("删除失败（文件可能被占用）：" + id); return; }

                    bool wasCurrent = PerfSession.CurrentPath == path;
                    if (wasCurrent) PerfSession.SetCurrent(null, "");
                    RefreshSnapshots();
                    if (wasCurrent) RefreshDetails();
                    SetStatus("已删除 " + id);
                });
                del.text = "✕";
                del.style.width = 22;
                del.style.fontSize = Theme.SizeSmall;
                del.style.marginRight = 0;
                del.style.marginBottom = 2;
                Theme.Rounded(del, 4f);
                row.Add(del);

                _snapshotHost.Add(row);
            }
        }

        void RefreshDetails()
        {
            BuildTabs();
            if (_detailHost == null) return;
            _detailHost.Clear();

            var snap = PerfSession.Current;
            // 没快照时明细区只剩一句空状态，没必要占半屏 —— 把高度还给对话区。
            // （有快照时它才参与 flexGrow 分配，2:3）
            _detailHost.style.flexGrow = snap == null ? 0 : 2;
            if (snap == null)
            {
                _detailHost.Add(Theme.EmptyState("◎", "还没有数据",
                    "点「跟随采集」后**工具会自动量基线并进入 Play**，你只管操作（战斗、开背包、切界面都算）；玩几秒后退出 Play 就会出结论；\n也可以从左上列表载入历史快照，或先跑一次「静态审计」（秒级、不需要进 Play）。",
                    "跟随采集", ToggleFollowCapture));
                return;
            }

            switch (_activeTab)
            {
                case "metrics": RenderMetrics(snap); break;
                case "frames": RenderFrames(snap); break;
                case "markers": RenderMarkers(snap); break;
                case "assets": RenderAssets(snap); break;
                case "scene": RenderScene(snap); break;
                case "code": RenderCode(snap); break;
                case "diff": RenderDiff(snap); break;
                case "fixlog": RenderFixLog(); break;
                case "notes": RenderNotes(snap); break;
                default: RenderFindings(snap); break;
            }
        }

        void RenderFindings(PerfSnapshot snap)
        {
            if (snap.findings.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("✓", "未发现超出预算的问题",
                    "当前快照的指标全部在预算内。想更严格就把左边的预算调小再「重新分析」；\n也可以用「静态审计」查资源与代码里的隐患。",
                    null, null, Theme.Good));
                return;
            }

            for (int i = 0; i < snap.findings.Count; i++)
            {
                var f = snap.findings[i];
                var card = new VisualElement();
                card.style.marginBottom = 6;
                Theme.Pad(card, 9, 9, 7, 7);
                card.style.backgroundColor = Theme.CardBgAlt;
                Theme.Rounded(card, 4f);
                card.style.borderLeftWidth = 4;
                card.style.borderLeftColor = SeverityColor(f.severity);

                var header = new Label("[" + SeverityLabel(f.severity) + "] " + f.title);
                header.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.style.whiteSpace = WhiteSpace.Normal;
                header.style.fontSize = Theme.SizeBody;
                header.style.color = Theme.Text;
                card.Add(header);

                if (!string.IsNullOrEmpty(f.detail))
                {
                    var detail = new Label(f.detail);
                    detail.style.whiteSpace = WhiteSpace.Normal;
                    detail.style.fontSize = Theme.SizeSmall;
                    detail.style.color = Theme.TextDim;
                    detail.style.marginTop = 3;
                    card.Add(detail);
                }

                if (!string.IsNullOrEmpty(f.recommendation))
                {
                    var rec = new Label("建议：" + f.recommendation);
                    rec.style.whiteSpace = WhiteSpace.Normal;
                    rec.style.color = new Color(0.62f, 0.85f, 0.68f);
                    card.Add(rec);
                }

                AddFixPlan(card, snap, f);

                var meta = new Label("分类 " + f.category + " · 置信度 " + (f.confidence * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                    + (string.IsNullOrEmpty(f.jumpTo) ? "" : " · 定位 " + f.jumpTo));
                meta.style.fontSize = 11;
                meta.style.color = new Color(0.58f, 0.62f, 0.68f);
                meta.style.whiteSpace = WhiteSpace.Normal;
                card.Add(meta);

                if (f.evidence.Count > 0)
                {
                    var fold = new Foldout();
                    fold.text = "证据链（" + f.evidence.Count + "）";
                    fold.value = false;
                    for (int k = 0; k < f.evidence.Count; k++)
                    {
                        var e = f.evidence[k];
                        var line = new Label("· [" + e.tool + "] " + e.metric + " = " + e.value
                            + (string.IsNullOrEmpty(e.unit) ? "" : " " + e.unit)
                            + (string.IsNullOrEmpty(e.threshold) ? "" : "（预算 " + e.threshold + "）")
                            + (string.IsNullOrEmpty(e.source) ? "" : "  @" + e.source));
                        line.style.fontSize = 11;
                        line.style.color = new Color(0.66f, 0.7f, 0.76f);
                        line.style.whiteSpace = WhiteSpace.Normal;
                        fold.Add(line);
                    }
                    card.Add(fold);
                }

                if (!string.IsNullOrEmpty(f.jumpTo))
                {
                    string jump = f.jumpTo;
                    var jumpButton = new Button(delegate { JumpTo(jump); });
                    jumpButton.text = "定位";
                    jumpButton.style.width = 60;
                    jumpButton.style.marginTop = 4;
                    card.Add(jumpButton);
                }

                _detailHost.Add(card);
            }
        }

        // =====================================================================
        // 一键修复计划（P4）
        //
        // 原则：面板只负责「展示计划 + 在用户点按钮后触发执行」。
        // 计划怎么算在 PerfFixPlanner（纯逻辑），怎么改在 PerfFixExecutor（唯一会改工程的地方），
        // 这里不自己拼改资源/改设置的代码。
        // =====================================================================

        void AddFixPlan(VisualElement card, PerfSnapshot snapshot, PerfFinding finding)
        {
            var plan = PerfFixPlanner.BuildPlan(snapshot, finding);
            if (plan == null || plan.steps.Count == 0) return;

            var box = new VisualElement();
            box.style.marginTop = 6;
            box.style.paddingLeft = 6;
            box.style.paddingRight = 6;
            box.style.paddingTop = 5;
            box.style.paddingBottom = 5;
            box.style.backgroundColor = new Color(0.13f, 0.145f, 0.17f);
            box.style.borderLeftWidth = 2;
            box.style.borderLeftColor = new Color(0.34f, 0.45f, 0.6f);

            var title = new Label("修复计划 · " + plan.Summary());
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 12;
            title.style.color = new Color(0.66f, 0.79f, 0.96f);
            box.Add(title);

            for (int i = 0; i < plan.steps.Count; i++)
                box.Add(BuildFixStepRow(plan.steps[i], finding.id, plan));

            if (plan.SafeStepCount > 1)
            {
                var all = new Button(delegate { ExecuteSafeSteps(plan); });
                all.text = "一键执行全部低风险项（" + plan.SafeStepCount + "）";
                all.style.marginTop = 5;
                box.Add(all);
            }

            card.Add(box);
        }

        VisualElement BuildFixStepRow(PerfFixStep step, string findingId, PerfFixPlan plan)
        {
            var row = new VisualElement();
            row.style.marginTop = 4;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.flexWrap = Wrap.Wrap;

            var badge = new Label(StepBadge(step));
            badge.style.fontSize = 10;
            badge.style.width = 62;
            badge.style.color = RiskColor(step.risk);
            head.Add(badge);

            var text = new Label(step.title);
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            head.Add(text);

            if (step.CanExecute)
            {
                var run = new Button(delegate { ExecuteStep(step, findingId, plan); });
                run.text = step.targetCount > 1 ? ("执行 " + step.targetCount + " 项") : "执行";
                run.style.width = 96;
                run.tooltip = step.detail
                    + (string.IsNullOrEmpty(step.expectedGain) ? "" : "\n预期收益：" + step.expectedGain)
                    + "\n风险：" + FixRisk.Label(step.risk);
                head.Add(run);
            }
            else if (step.kind == FixKind.Navigate && step.targets.Count > 0)
            {
                string target = step.targets[0];
                var go = new Button(delegate { JumpTo(target); });
                go.text = "定位";
                go.style.width = 96;
                head.Add(go);
            }
            else
            {
                var manual = new Label("需人工");
                manual.style.fontSize = 10;
                manual.style.color = new Color(0.6f, 0.62f, 0.66f);
                manual.style.width = 96;
                manual.style.unityTextAlign = TextAnchor.MiddleRight;
                head.Add(manual);
            }

            row.Add(head);

            var detail = new Label(step.detail
                + (string.IsNullOrEmpty(step.expectedGain) ? "" : "  ·  " + step.expectedGain));
            detail.style.fontSize = 11;
            detail.style.color = new Color(0.68f, 0.71f, 0.76f);
            detail.style.whiteSpace = WhiteSpace.Normal;
            detail.style.marginLeft = 62;
            row.Add(detail);

            if (step.batch && step.targets.Count > 0)
            {
                var fold = new Foldout();
                fold.text = "影响目标（" + step.targetCount + "）";
                fold.value = false;
                fold.style.marginLeft = 62;
                fold.style.fontSize = 11;
                for (int i = 0; i < step.targets.Count; i++)
                    fold.Add(new Label(step.targets[i]));
                if (step.targetCount > step.targets.Count)
                    fold.Add(new Label("…等共 " + step.targetCount + " 项"));
                row.Add(fold);
            }

            return row;
        }

        void ExecuteStep(PerfFixStep step, string findingId, PerfFixPlan plan)
        {
            if (!ConfirmStep(step, plan)) return;
            RunFixSteps(new List<PerfFixStep> { step }, findingId, plan, "一键修复");
        }

        void ExecuteSafeSteps(PerfFixPlan plan)
        {
            var safe = new List<PerfFixStep>();
            for (int i = 0; i < plan.steps.Count; i++)
                if (plan.steps[i].CanExecute && plan.steps[i].IsSafe) safe.Add(plan.steps[i]);

            if (safe.Count == 0) return;
            if (!ConfirmBatch(safe, plan)) return;
            RunFixSteps(safe, plan.findingId, plan, "批量修复");
        }

        bool ConfirmStep(PerfFixStep step, PerfFixPlan plan)
        {
            var text = new StringBuilder();
            text.Append("将要执行：").Append(step.title).Append("\n\n");
            text.Append(step.detail).Append("\n\n");
            text.Append("影响范围：").Append(step.targetCount).Append(" 项\n");
            for (int i = 0; i < step.targets.Count && i < 6; i++)
                text.Append("  · ").Append(step.targets[i]).Append('\n');
            if (step.targetCount > 6) text.Append("  …\n");

            if (!string.IsNullOrEmpty(step.expectedGain))
                text.Append("\n预期收益：").Append(step.expectedGain).Append('\n');

            text.Append("\n风险：").Append(FixRisk.Label(step.risk));
            text.Append(step.reversible ? "（会写入变更日志，可撤销）" : "（不可撤销，建议先提交版本控制）");
            text.Append("\n来源结论：").Append(plan.findingId);

            return EditorUtility.DisplayDialog("PerfAgent 一键修复", text.ToString(), "执行", "取消");
        }

        bool ConfirmBatch(List<PerfFixStep> steps, PerfFixPlan plan)
        {
            var text = new StringBuilder();
            text.Append("即将执行 ").Append(steps.Count).Append(" 个低风险修复项：\n\n");

            int totalTargets = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                totalTargets += steps[i].targetCount;
                text.Append("· ").Append(steps[i].title)
                    .Append("（").Append(steps[i].targetCount).Append(" 项）\n");
            }

            text.Append("\n合计影响 ").Append(totalTargets).Append(" 项。\n");
            text.Append("全部会写入变更日志，可逐条撤销。\n");
            text.Append("来源结论：").Append(plan.findingId);

            return EditorUtility.DisplayDialog("PerfAgent 一键修复（低风险批量）", text.ToString(), "全部执行", "取消");
        }

        void RunFixSteps(List<PerfFixStep> steps, string findingId, PerfFixPlan plan, string title)
        {
            PerfFixExecutor.Progress = delegate (int index, int total, string target)
            {
                EditorUtility.DisplayProgressBar("PerfAgent " + title, target, (index + 1f) / Mathf.Max(1, total));
            };

            var executed = new List<PerfFixOutcome>();
            int changed = 0;
            int failed = 0;

            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("PerfAgent " + title, steps[i].title, (i + 1f) / steps.Count);
                    var outcome = PerfFixExecutor.Execute(steps[i], findingId);
                    executed.Add(outcome);
                    changed += outcome.changedCount;
                    if (!outcome.success && outcome.changedCount == 0) failed++;
                }
            }
            finally
            {
                PerfFixExecutor.Progress = null;
                EditorUtility.ClearProgressBar();
            }

            // 结果写进对话区，保证「改了什么」可回溯
            var summary = new StringBuilder();
            summary.Append("\n**").Append(title).Append("**：共修改 ").Append(changed).Append(" 项");
            if (failed > 0) summary.Append("，").Append(failed).Append(" 项未变更");
            summary.Append("\n");
            for (int i = 0; i < executed.Count; i++)
            {
                summary.Append("- ").Append(executed[i].Describe()).Append('\n');
                for (int k = 0; k < executed[i].details.Count && k < 5; k++)
                    summary.Append("    · ").Append(executed[i].details[k]).Append('\n');
            }
            bool anyUndo = false;
            for (int i = 0; i < executed.Count; i++)
                if (PerfFixExecutor.CanUndo(executed[i].record)) { anyUndo = true; break; }
            summary.Append(anyUndo ? "可在「变更」标签里撤销。\n" : "（该动作不可撤销，建议用版本控制回滚）\n");
            AppendTranscript(summary.ToString());

            SetStatus(changed > 0 ? ("修复完成：修改 " + changed + " 项") : "修复完成：没有需要修改的项");

            // 导入设置变了，重新跑一次静态审计，让结论立即反映现状
            if (changed > 0) ReauditAfterFix();
            RefreshDetails();
        }

        void ReauditAfterFix()
        {
            if (PerfSession.Current == null) return;
            PerfPipeline.StaticAudit(false, SetStatus);
        }

        void RenderFixLog()
        {
            var records = PerfChangeLog.Load();
            if (records.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("↺", "还没有执行过一键修复",
                    "在「结论」标签里，每条结论下方都会给出修复计划：可执行步骤带「执行」按钮，\n点击前会弹窗列出将要修改的目标，改完可在这里撤销。"));
                return;
            }

            _detailHost.Add(Theme.Hint(records.Count + " 条记录（新的在下）· 日志：ProjectSettings/PerfAgent/FixLog.json"));

            var clear = new Button(delegate
            {
                if (EditorUtility.DisplayDialog("PerfAgent", "清空修复日志？\n（只删日志，不会还原已做的修改）", "清空", "取消"))
                {
                    PerfChangeLog.Clear();
                    RefreshDetails();
                }
            });
            clear.text = "清空日志";
            clear.style.marginTop = 4;
            clear.style.marginBottom = 8;
            _detailHost.Add(clear);

            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];

                var card = new VisualElement();
                card.style.marginBottom = 5;
                card.style.paddingLeft = 8;
                card.style.paddingRight = 8;
                card.style.paddingTop = 5;
                card.style.paddingBottom = 5;
                card.style.backgroundColor = new Color(0.17f, 0.18f, 0.2f);
                card.style.borderLeftWidth = 4;
                card.style.borderLeftColor = record.undone
                    ? new Color(0.5f, 0.5f, 0.55f)
                    : RiskColor(record.risk);

                var head = new Label(record.Label() + (record.undone ? "  [已撤销]" : ""));
                head.style.unityFontStyleAndWeight = FontStyle.Bold;
                head.style.whiteSpace = WhiteSpace.Normal;
                card.Add(head);

                var meta = new Label("动作 " + record.actionId
                    + (string.IsNullOrEmpty(record.findingId) ? "" : " · 来源 " + record.findingId)
                    + " · " + FixRisk.Label(record.risk));
                meta.style.fontSize = 11;
                meta.style.color = new Color(0.6f, 0.63f, 0.68f);
                meta.style.whiteSpace = WhiteSpace.Normal;
                card.Add(meta);

                if (!string.IsNullOrEmpty(record.message))
                {
                    var msg = new Label(record.message);
                    msg.style.fontSize = 11;
                    msg.style.color = new Color(0.7f, 0.74f, 0.79f);
                    msg.style.whiteSpace = WhiteSpace.Normal;
                    card.Add(msg);
                }

                if (PerfFixExecutor.CanUndo(record))
                {
                    var undo = new Button(delegate { RevertFix(record); });
                    undo.text = "撤销这次修改";
                    undo.style.marginTop = 4;
                    card.Add(undo);
                }

                _detailHost.Add(card);
            }
        }

        void RevertFix(PerfFixRecord record)
        {
            if (!EditorUtility.DisplayDialog("PerfAgent 撤销",
                "撤销「" + record.title + "」？\n\n会把该次修改涉及的 " + record.changedCount + " 项还原回修改前的值。",
                "撤销", "取消")) return;

            PerfFixOutcome outcome;
            try
            {
                EditorUtility.DisplayProgressBar("PerfAgent 撤销", record.title, 0.5f);
                PerfFixExecutor.Progress = delegate (int index, int total, string target)
                {
                    EditorUtility.DisplayProgressBar("PerfAgent 撤销", target, (index + 1f) / Mathf.Max(1, total));
                };
                outcome = PerfFixExecutor.Revert(record);
            }
            finally
            {
                PerfFixExecutor.Progress = null;
                EditorUtility.ClearProgressBar();
            }

            AppendTranscript("\n**撤销**：" + outcome.Describe() + "\n");
            SetStatus(outcome.success ? "已撤销" : "撤销未生效");

            if (outcome.success) ReauditAfterFix();
            RefreshDetails();
        }

        static string StepBadge(PerfFixStep step)
        {
            if (!step.CanExecute) return step.kind == FixKind.Navigate ? "定位" : "人工";
            if (step.risk == FixRisk.Safe) return "① 低风险";
            if (step.risk == FixRisk.Moderate) return "② 中风险";
            return "③ 高风险";
        }

        static Color RiskColor(string risk)
        {
            if (risk == FixRisk.Risky) return new Color(1f, 0.48f, 0.45f);
            if (risk == FixRisk.Moderate) return new Color(1f, 0.83f, 0.48f);
            return new Color(0.55f, 0.87f, 0.62f);
        }

        static void JumpTo(string target)
        {
            if (string.IsNullOrEmpty(target)) return;

            if (target.Contains(":"))
            {
                int colon = target.LastIndexOf(':');
                string file = target.Substring(0, colon);
                int line;
                if (int.TryParse(target.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out line))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(file);
                    if (obj != null)
                    {
                        AssetDatabase.OpenAsset(obj, line);
                        return;
                    }
                }
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(target) != null)
            {
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(target));
                return;
            }

            if (PerfSession.Current != null)
            {
                var issues = PerfSession.Current.sceneIssues;
                for (int i = 0; i < issues.Count; i++)
                {
                    if (issues[i].hierarchyPath != target) continue;
                    var segments = target.Split('/');
                    var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                    for (int r = 0; r < roots.Length; r++)
                    {
                        if (roots[r].name != segments[0]) continue;
                        var found = roots[r].transform.Find(target.Substring(segments[0].Length).TrimStart('/'));
                        if (found != null)
                        {
                            Selection.activeGameObject = found.gameObject;
                            EditorGUIUtility.PingObject(found.gameObject);
                            return;
                        }
                    }
                }
            }

            UnityEngine.Debug.Log("[PerfAgent] 无法定位：" + target);
        }

        void RenderMetrics(PerfSnapshot snap)
        {
            for (int i = 0; i < snap.metrics.Count; i++)
            {
                var m = snap.metrics[i];
                var row = new Label(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,12} {2}   {3}",
                    m.name,
                    m.value.ToString("0.##", CultureInfo.InvariantCulture),
                    m.unit,
                    string.IsNullOrEmpty(m.budget) ? "" : "预算 " + m.budget + " " + m.budgetUnit));
                row.style.whiteSpace = WhiteSpace.Normal;
                row.style.color = SeverityColor(m.severity);
                _detailHost.Add(row);
            }
        }

        void RenderFrames(PerfSnapshot snap)
        {
            if (snap.frames.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("—", "没有帧数据",
                    "这份快照只做了静态审计（资源 / 场景 / 代码），不含运行时帧。\n点「跟随采集」—— 工具会自动进 Play 并录一段。"));
                return;
            }

            string allocCaliber;
            double allocAvg = snap.AverageAllocPerFrame(out allocCaliber);
            _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                "均值 {0:0.##} ms · P50 {1:0.##} · P95 {2:0.##} · 峰值 {3:0.##}\n平均每帧分配 {4}",
                snap.FrameTimeAvgMs(), snap.FrameTimePercentileMs(50), snap.FrameTimePercentileMs(95), snap.FrameTimeMaxMs(),
                double.IsNaN(allocAvg) ? "不可用" + allocCaliber : string.Format(CultureInfo.InvariantCulture, "{0:0} B {1}", allocAvg, allocCaliber))));

            _detailHost.Add(new Label(""));
            _detailHost.Add(new Label("最慢的 20 帧（`—` = 该帧这一项没采到）："));
            var frames = new List<FrameStat>(snap.frames);
            frames.Sort(delegate (FrameStat a, FrameStat b) { return b.deltaMs.CompareTo(a.deltaMs); });
            for (int i = 0; i < frames.Count && i < 20; i++)
            {
                var f = frames[i];
                long alloc = f.DisplayAllocBytes;
                _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                    "帧 {0,-8} {1,8:0.##} ms  分配 {2,10}  DrawCall {3,5}  TempAlloc {4}",
                    f.frame, f.deltaMs,
                    alloc > 0 ? alloc.ToString(CultureInfo.InvariantCulture) + " B" : "—",
                    f.drawCalls > 0 ? f.drawCalls.ToString(CultureInfo.InvariantCulture) : "—",
                    f.tempAllocBytes > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", f.tempAllocBytes / 1048576.0) : "—")));
            }
        }

        void RenderMarkers(PerfSnapshot snap)
        {
            if (snap.markers.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("—", "无 Marker 数据",
                    "可能是采集期间 Profiler 未记录，或该 Unity 版本的层级视图 API 形态不同。\n点菜单「Tools > PerfAgent > API 探针」可以确认当前版本到底支持哪些成员。"));
                return;
            }
            for (int i = 0; i < snap.markers.Count; i++)
            {
                var m = snap.markers[i];
                _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                    "{0,6:0.##} ms (self) {1,8:0.##} ms (total)  GC {2,8:0.0} KB  calls {3,7}  {4}",
                    m.selfMs, m.totalMs, m.gcAllocBytes / 1024.0, m.calls, m.name)));
            }
        }

        void RenderAssets(PerfSnapshot snap)
        {
            if (snap.assetIssues.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("✓", "未发现资源导入问题",
                    "纹理 / 模型 / 音频的导入设置都在合理范围。", null, null, Theme.Good));
                return;
            }

            for (int i = 0; i < snap.assetIssues.Count && i < 200; i++)
            {
                var a = snap.assetIssues[i];
                _detailHost.Add(Theme.IssueRow(
                    Theme.SeverityTag(SeverityLabel(a.severity), SeverityColor(a.severity)) + " " + a.path,
                    SeverityColor(a.severity),
                    a.issue + "\n→ " + a.suggestion));
            }
        }

        void RenderScene(PerfSnapshot snap)
        {
            if (snap.sceneIssues.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("✓", "未发现场景 / 物理反模式",
                    "组件反模式、批处理漏网与物理配置都没找到问题。", null, null, Theme.Good));
                return;
            }

            for (int i = 0; i < snap.sceneIssues.Count && i < 200; i++)
            {
                var v = snap.sceneIssues[i];
                _detailHost.Add(Theme.IssueRow(
                    Theme.SeverityTag(SeverityLabel(v.severity), SeverityColor(v.severity))
                        + " " + v.hierarchyPath + "  ·  " + v.componentType,
                    SeverityColor(v.severity),
                    v.issue + "\n→ " + v.suggestion));
            }
        }

        void RenderCode(PerfSnapshot snap)
        {
            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.flexWrap = Wrap.Wrap;
            head.style.alignItems = Align.Center;
            head.style.marginBottom = 6;
            head.Add(Theme.Secondary("复制修复建议", CopyFixSuggestions));
            var tip = new Label("本地给清单（文件:行 + 规则建议）；开了 AI 会给到「改成什么代码」。");
            tip.style.fontSize = Theme.SizeSmall;
            tip.style.color = Theme.TextFaint;
            tip.style.whiteSpace = WhiteSpace.Normal;
            head.Add(tip);
            _detailHost.Add(head);

            if (snap.codeIssues.Count == 0)
            {
                _detailHost.Add(Theme.EmptyState("✓", "未发现脚本反模式",
                    "每帧方法体里的堆分配、查找类 API、LINQ 等都没扫到。", null, null, Theme.Good));
                return;
            }

            for (int i = 0; i < snap.codeIssues.Count && i < 300; i++)
            {
                var c = snap.codeIssues[i];
                _detailHost.Add(Theme.IssueRow(
                    Theme.SeverityTag(SeverityLabel(c.severity), SeverityColor(c.severity))
                        + " " + c.file + ":" + c.line + "  ·  " + c.pattern,
                    SeverityColor(c.severity),
                    c.snippet + "\n→ " + c.suggestion));
            }
        }

        void RenderNotes(PerfSnapshot snap)
        {
            _detailHost.Add(new Label("快照 id：" + snap.id));
            _detailHost.Add(new Label("采集器：" + string.Join(", ", snap.capturedSources.ToArray())));
            _detailHost.Add(new Label(""));
            for (int i = 0; i < snap.notes.Count; i++)
                _detailHost.Add(new Label("· " + snap.notes[i]));
        }

        // =====================================================================
        // 会话对比（P4）
        // =====================================================================

        void RenderDiff(PerfSnapshot snap)
        {
            var paths = PerfSnapshotStore.List();
            if (paths.Count < 2)
            {
                _detailHost.Add(new Label("至少需要两份快照才能对比。再抓一次帧（或先加载一份历史快照作为当前）。"));
                return;
            }

            string baselinePath = ResolveBaselinePath(paths);
            if (string.IsNullOrEmpty(baselinePath))
            {
                _detailHost.Add(new Label("除当前快照外没有其它快照，无法对比。"));
                return;
            }

            _detailHost.Add(SectionTitle("基准快照（点击切换）"));
            var picker = new VisualElement();
            picker.style.flexDirection = FlexDirection.Row;
            picker.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < paths.Count && i < 12; i++)
            {
                string p = paths[i];
                if (p == PerfSession.CurrentPath) continue;
                bool selected = p == baselinePath;
                var button = new Button(delegate
                {
                    _baselinePath = p;
                    RefreshDetails();
                });
                button.text = System.IO.Path.GetFileNameWithoutExtension(p);
                button.style.marginRight = 4;
                button.style.marginBottom = 4;
                if (selected)
                {
                    button.style.backgroundColor = new Color(0.24f, 0.42f, 0.68f);
                    button.style.color = Color.white;
                }
                picker.Add(button);
            }
            _detailHost.Add(picker);

            var baseline = PerfSnapshotStore.Load(baselinePath);
            if (baseline == null)
            {
                _detailHost.Add(new Label("基准快照读取失败：" + baselinePath));
                return;
            }

            var diff = PerfDiff.Compare(baseline, snap);
            if (!diff.Comparable)
            {
                _detailHost.Add(new Label(diff.error));
                return;
            }

            var verdict = new Label(diff.Verdict() + "（严重度加权分 " + diff.Score() + "）");
            verdict.style.fontSize = 16;
            verdict.style.unityFontStyleAndWeight = FontStyle.Bold;
            verdict.style.color = VerdictColor(diff.Score());
            verdict.style.marginBottom = 6;
            _detailHost.Add(verdict);

            _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                "恶化指标 {0} 项 · 改善指标 {1} 项 · 新增结论 {2} 条 · 消除结论 {3} 条 · 未变结论 {4} 条",
                diff.RegressedMetricCount, diff.ImprovedMetricCount,
                diff.newFindings.Count, diff.resolvedFindings.Count, diff.persistingFindings.Count)));

            var copy = new Button(delegate { AppendTranscript("\n" + diff.ToMarkdown() + "\n"); SetStatus("对比结论已写入对话"); });
            copy.text = "把对比结论写入对话";
            copy.style.marginTop = 6;
            copy.style.marginBottom = 8;
            _detailHost.Add(copy);

            if (diff.SignificantlyChangedCount > 0)
            {
                _detailHost.Add(SectionTitle("指标变化"));
                for (int i = 0; i < diff.metrics.Count; i++)
                {
                    var m = diff.metrics[i];
                    if (!m.changed) continue;
                    var row = new Label(m.Format());
                    row.style.whiteSpace = WhiteSpace.Normal;
                    row.style.color = m.regressed ? SeverityColor(Severity.Error) : new Color(0.5f, 0.85f, 0.6f);
                    _detailHost.Add(row);
                }
            }

            RenderDiffFindings("新增结论（回归）", diff.newFindings);
            RenderDiffFindings("结论加重", diff.worsenedFindings);
            RenderDiffFindings("结论减轻", diff.improvedFindings);
            RenderDiffFindings("已消除结论", diff.resolvedFindings);

            if (diff.newAssetIssues.Count + diff.resolvedAssetIssues.Count +
                diff.newSceneIssues.Count + diff.resolvedSceneIssues.Count +
                diff.newCodeIssues.Count + diff.resolvedCodeIssues.Count > 0)
            {
                _detailHost.Add(SectionTitle("问题清单变化"));
                AddCountRow("资源问题", diff.resolvedAssetIssues.Count, diff.newAssetIssues.Count);
                AddCountRow("场景问题", diff.resolvedSceneIssues.Count, diff.newSceneIssues.Count);
                AddCountRow("代码问题", diff.resolvedCodeIssues.Count, diff.newCodeIssues.Count);
            }
        }

        string ResolveBaselinePath(List<string> paths)
        {
            string current = PerfSession.CurrentPath;
            if (!string.IsNullOrEmpty(_baselinePath) && _baselinePath != current && paths.Contains(_baselinePath))
                return _baselinePath;

            for (int i = 0; i < paths.Count; i++)
                if (paths[i] != current) return paths[i];
            return "";
        }

        void RenderDiffFindings(string title, List<PerfFinding> list)
        {
            if (list.Count == 0) return;
            _detailHost.Add(SectionTitle(title));
            for (int i = 0; i < list.Count; i++)
            {
                var label = new Label("[" + SeverityLabel(list[i].severity) + "] " + list[i].title);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.color = SeverityColor(list[i].severity);
                _detailHost.Add(label);
            }
        }

        void AddCountRow(string label, int resolved, int added)
        {
            var text = new StringBuilder(label).Append("：消除 ").Append(resolved).Append(" 条，新增 ").Append(added).Append(" 条");
            _detailHost.Add(new Label(text.ToString()));
        }

        static Color VerdictColor(int score)
        {
            if (score >= 4) return new Color(1f, 0.45f, 0.45f);
            if (score >= 1) return new Color(1f, 0.83f, 0.48f);
            if (score <= -4) return new Color(0.45f, 0.9f, 0.55f);
            if (score <= -1) return new Color(0.65f, 0.88f, 0.6f);
            return new Color(0.72f, 0.76f, 0.82f);
        }

        static Color SeverityColor(string severity)
        {
            if (severity == Severity.Error) return new Color(1f, 0.45f, 0.45f);
            if (severity == Severity.Warn) return new Color(1f, 0.83f, 0.48f);
            return new Color(0.72f, 0.76f, 0.82f);
        }

        static string SeverityLabel(string severity)
        {
            if (severity == Severity.Error) return "严重";
            if (severity == Severity.Warn) return "警告";
            return "提示";
        }
    }
}

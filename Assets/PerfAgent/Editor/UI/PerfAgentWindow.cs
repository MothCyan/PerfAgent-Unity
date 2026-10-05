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

namespace PerfAgent.UI
{
    /// <summary>性能诊断面板：采集 / 结论 / 明细 / 对话，全部在一个可停靠窗口内。</summary>
    public class PerfAgentWindow : EditorWindow
    {
        const string MenuRoot = "Tools/PerfAgent/";

        // 布局
        Label _status;
        VisualElement _liveHost;
        VisualElement[] _liveBars;
        Label _liveLabel;
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

        [MenuItem(MenuRoot + "打开性能诊断面板 %#p", false, 100)]
        public static void Open()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.minSize = new Vector2(900, 600);
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
        /// 跟随采集：**你自己进 Play 操作，工具在旁边记录**，时长不限。
        ///
        /// 按钮行为随状态变：
        ///   未开始 → 进入待命（下一次进 Play 自动开始记录）
        ///   待命中 → 取消
        ///   采集中 → 结束并立即出快照（不会退出 Play，你可以接着玩）
        /// </summary>
        [MenuItem(MenuRoot + "跟随采集（自己操作，时长不限）", false, 105)]
        public static void FollowCaptureMenu()
        {
            var window = GetWindow<PerfAgentWindow>("性能诊断");
            window.ToggleFollowCapture();
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
                : "跟随采集已待命 —— 现在进入 Play 就会自动开始记录，时长不限。");
        }

        void OnEnable()
        {
            PerfSession.Changed += OnSessionChanged;
            PerfAgentSettingsWindow.Changed += RefreshLlmStatus;
            Agent.OnStatus += SetStatus;
            EditorApplication.update += PollFollowCapture;
            FollowCapture.Changed += OnFollowCaptureChanged;
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
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;

            root.Add(BuildToolbar());

            _status = new Label("就绪");
            _status.style.marginTop = 4;
            _status.style.marginBottom = 2;
            _status.style.color = new Color(0.75f, 0.78f, 0.82f);
            root.Add(_status);

            _liveHost = BuildLiveStrip();
            _liveHost.style.display = DisplayStyle.None;   // 有样本了才显示
            root.Add(_liveHost);

            var columns = new VisualElement();
            columns.style.flexDirection = FlexDirection.Row;
            columns.style.flexGrow = 1;
            columns.style.marginTop = 6;
            root.Add(columns);

            // ---- 左栏 ----
            var left = new VisualElement();
            left.style.width = 300;
            left.style.marginRight = 8;
            left.style.flexDirection = FlexDirection.Column;
            columns.Add(left);

            left.Add(SectionTitle("分析标签"));
            _tabRow = new VisualElement();
            _tabRow.style.flexDirection = FlexDirection.Row;
            _tabRow.style.flexWrap = Wrap.Wrap;
            left.Add(_tabRow);
            BuildTabs();

            left.Add(SectionTitle("快照"));
            _snapshotHost = new ScrollView(ScrollViewMode.Vertical);
            _snapshotHost.style.maxHeight = 180;
            left.Add(_snapshotHost);

            left.Add(SectionTitle("性能预算"));
            left.Add(BuildBudgetEditor());

            // ---- 右栏 ----
            var right = new VisualElement();
            right.style.flexGrow = 1;
            right.style.flexDirection = FlexDirection.Column;
            columns.Add(right);

            _detailHost = new ScrollView(ScrollViewMode.Vertical);
            _detailHost.style.flexGrow = 1;
            _detailHost.style.backgroundColor = new Color(0.14f, 0.15f, 0.17f);
            _detailHost.style.paddingLeft = 8;
            _detailHost.style.paddingRight = 8;
            _detailHost.style.paddingTop = 6;
            right.Add(_detailHost);

            right.Add(BuildChatPanel());

            RefreshSnapshots();
            RefreshDetails();
            RefreshLlmStatus();
            AppendTranscript("**性能诊断 Agent**\n\n点「跟随采集」后**自己进 Play 操作**（战斗、开背包、切界面都算），"
                + "面板上方会实时显示帧率波形；结束时再点一次「跟随采集」或直接退出 Play，数据会自动分析。\n"
                + "然后可以直接提问，例如：\n- 为什么会有周期性卡顿？\n- 内存的大头在哪里？\n- 每帧的分配是从哪来的？\n"
                + "\n要贴给别人（或丢给外部 AI 继续追问），点工具栏「复制结论」。\n");
            RestoreLatestConversation();
        }

        /// <summary>刷新 LLM 状态条。读的是真实配置，所以状态不会与实际行为脱节。</summary>
        void RefreshLlmStatus()
        {
            if (_llmStatus == null) return;

            var cfg = PerfAgentSettings.Config;
            if (cfg.localOnlyNoLlm)
            {
                _llmStatus.text = "LLM：纯本地模式（只用规则引擎，不外传任何数据）";
                _llmStatus.style.color = new Color(0.62f, 0.66f, 0.72f);
            }
            else if (!cfg.HasApiKey && !LlmClient.IsLocalEndpoint(cfg.endpoint))
            {
                _llmStatus.text = "LLM：未配置 API Key（仍可用，回答会退化为本地规则引擎结论）";
                _llmStatus.style.color = new Color(1f, 0.83f, 0.48f);
            }
            else
            {
                string source = cfg.ApiKeySource;
                _llmStatus.text = "LLM：" + cfg.model
                    + (string.IsNullOrEmpty(source) ? "" : "（Key 来源：" + source + "）");
                _llmStatus.style.color = new Color(0.55f, 0.87f, 0.62f);
            }
        }

        /// <summary>
        /// 跟随采集的状态栏。
        ///
        /// 放在 EditorApplication.update 上是因为采集期间要实时显示已记录帧数；
        /// 限流与「帧数没变就不刷」见 _nextFollowPoll 的注释（刷界面本身会产生分配）。
        /// </summary>
        void PollFollowCapture()
        {
            if (FollowCapture.Capturing)
            {
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

                if (frames != _lastFollowFrames)
                {
                    _lastFollowFrames = frames;
                    string summary = _live.Summary(true);
                    SetStatus(summary.Length > 0
                        ? summary
                        : ("跟随采集中（你自己操作）：Profiler 已记录 " + frames + " 帧。"));
                }
                return;
            }

            if (_liveActive)
            {
                // 从「采集中」变为「已结束」：冻结波形（保留最后一段曲线），并补一次终态提示
                _liveActive = false;
                _lastFollowFrames = -1;
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
                    SetStatus("跟随采集已待命：进入 Play 模式后会自动开始记录。点「跟随采集」可取消。");
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
            host.style.marginTop = 4;
            host.style.paddingLeft = 6;
            host.style.paddingRight = 6;
            host.style.paddingTop = 4;
            host.style.paddingBottom = 4;
            host.style.backgroundColor = new Color(0.11f, 0.12f, 0.14f);

            _liveLabel = new Label("");
            _liveLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _liveLabel.style.color = new Color(0.85f, 0.88f, 0.95f);
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
            if (ms >= 33.0) return new Color(0.90f, 0.35f, 0.32f);
            if (ms >= 16.7) return new Color(0.92f, 0.78f, 0.30f);
            return new Color(0.35f, 0.75f, 0.45f);
        }


        VisualElement BuildToolbar()
        {
            var bar = new VisualElement();            bar.style.flexDirection = FlexDirection.Row;
            bar.style.flexWrap = Wrap.Wrap;
            bar.style.alignItems = Align.Center;

            bar.Add(ToolbarButton("跟随采集", ToggleFollowCapture));
            bar.Add(ToolbarButton("复制结论", CopyReport));
            bar.Add(ToolbarButton("静态审计", RunStaticAudit));
            bar.Add(ToolbarButton("重新分析", delegate
            {
                if (PerfSession.Current == null) { SetStatus("没有快照"); return; }
                PerfPipeline.Analyze(PerfSession.Current);
                PerfPipeline.SaveAndSetCurrent(PerfSession.Current);
                SetStatus("已重新分析");
            }));
            bar.Add(ToolbarButton("导出 MD", delegate { Export(false); }));
            bar.Add(ToolbarButton("导出 HTML", delegate { Export(true); }));
            bar.Add(ToolbarButton("LLM 配置", PerfAgentSettingsWindow.Open));
            bar.Add(ToolbarButton("新会话", NewConversation));
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
            var button = new Button(delegate
            {
                _activeTab = id;
                RefreshDetails();
            });
            button.text = label;
            button.style.marginRight = 4;
            button.style.marginBottom = 4;
            button.style.paddingLeft = 8;
            button.style.paddingRight = 8;
            if (_activeTab == id)
            {
                button.style.backgroundColor = new Color(0.24f, 0.42f, 0.68f);
                button.style.color = Color.white;
            }
            _tabRow.Add(button);
        }

        VisualElement BuildBudgetEditor()
        {
            var box = new VisualElement();
            var b = PerfAgentSettings.Config.budget;

            box.Add(BudgetField("目标帧率", b.targetFrameRate, delegate (double v) { b.targetFrameRate = (float)v; Apply(); }));
            box.Add(BudgetField("Draw Call", b.maxDrawCalls, delegate (double v) { b.maxDrawCalls = (int)v; Apply(); }));
            box.Add(BudgetField("SetPass Call", b.maxSetPassCalls, delegate (double v) { b.maxSetPassCalls = (int)v; Apply(); }));
            box.Add(BudgetField("三角面", b.maxTriangles, delegate (double v) { b.maxTriangles = (long)v; Apply(); }));
            box.Add(BudgetField("每帧分配 (B)", b.maxManagedAllocBytesPerFrame, delegate (double v) { b.maxManagedAllocBytesPerFrame = (long)v; Apply(); }));
            box.Add(BudgetField("纹理内存 (MB)", b.maxTextureMemoryMB, delegate (double v) { b.maxTextureMemoryMB = (long)v; Apply(); }));
            box.Add(BudgetField("TempAlloc (MB)", b.maxTempAllocatorMB, delegate (double v) { b.maxTempAllocatorMB = (long)v; Apply(); }));
            return box;
        }

        static VisualElement BudgetField(string label, double value, Action<double> onChange)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var lbl = new Label(label);
            lbl.style.width = 110;
            lbl.style.fontSize = 11;
            row.Add(lbl);

            var field = new DoubleField();
            field.value = value;
            field.style.flexGrow = 1;
            field.RegisterValueChangedCallback(delegate (ChangeEvent<double> e)
            {
                if (Math.Abs(e.newValue - e.previousValue) < 1e-9) return;
                onChange(e.newValue);
            });
            row.Add(field);
            row.style.marginBottom = 2;
            return row;
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
            var panel = new VisualElement();
            panel.style.marginTop = 6;

            // ---- LLM 状态条：未配置时给出就地入口，而不是等用户发完消息才报错 ----
            var llmBar = new VisualElement();
            llmBar.style.flexDirection = FlexDirection.Row;
            llmBar.style.alignItems = Align.Center;
            llmBar.style.marginBottom = 3;

            _llmStatus = new Label();
            _llmStatus.style.flexGrow = 1;
            _llmStatus.style.flexShrink = 1;
            _llmStatus.style.fontSize = 11;
            _llmStatus.style.whiteSpace = WhiteSpace.Normal;
            llmBar.Add(_llmStatus);

            var configure = new Button(delegate { PerfAgentSettingsWindow.Open(); });
            configure.text = "LLM 配置";
            configure.style.width = 92;
            llmBar.Add(configure);
            panel.Add(llmBar);

            _transcriptScroll = new ScrollView(ScrollViewMode.Vertical);
            _transcriptScroll.style.height = 150;
            _transcriptScroll.style.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            _transcriptScroll.style.paddingLeft = 8;
            _transcriptScroll.style.paddingRight = 8;
            _transcriptScroll.style.paddingTop = 6;
            panel.Add(_transcriptScroll);

            _transcript = new Label();
            _transcript.style.whiteSpace = WhiteSpace.Normal;
            _transcriptScroll.Add(_transcript);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4;
            panel.Add(row);

            _input = new TextField();
            _input.multiline = true;
            _input.style.flexGrow = 1;
            _input.style.height = 56;
            _input.RegisterCallback<KeyDownEvent>(delegate (KeyDownEvent evt)
            {
                if ((evt.ctrlKey || evt.commandKey) && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter))
                {
                    Send();
                    evt.StopPropagation();
                }
            });
            row.Add(_input);

            _sendButton = new Button(Send);
            _sendButton.text = "发送\n(Ctrl+Enter)";
            _sendButton.style.width = 92;
            _sendButton.style.height = 56;
            _sendButton.style.marginLeft = 4;
            row.Add(_sendButton);

            return panel;
        }

        static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 8;
            label.style.marginBottom = 2;
            label.style.color = new Color(0.62f, 0.72f, 0.9f);
            return label;
        }

        static Button ToolbarButton(string text, Action onClick)
        {
            var b = new Button(onClick);
            b.text = text;
            b.style.marginLeft = 4;
            b.style.marginRight = 2;
            return b;
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
                SetStatus("没有快照可复制：先点「跟随采集」跑一段，或从左上列表载入历史快照。");
                return;
            }

            string md = PerfReportExporter.ToMarkdown(snap);
            EditorGUIUtility.systemCopyBuffer = md;
            SetStatus("已复制 Markdown 报告（" + md.Length + " 字符）：环境 / 指标 / 结论 / 修复计划都在里面。");
        }

        void Send()
        {
            var text = _input.value;
            if (string.IsNullOrEmpty(text)) return;

            _input.value = "";
            AppendTranscript("\n**我**：" + text + "\n\n**Agent**：");
            _sendButton.SetEnabled(false);

            if (!PerfAgentSettings.Config.HasApiKey)
            {
                AppendTranscript(ScriptOnlyAnswer(text));
                _sendButton.SetEnabled(true);
                SetStatus("已用本地规则引擎回答（未配置 LLM）");
                SaveConversation();
                return;
            }

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
                    _sendButton.SetEnabled(true);
                    SetStatus("完成");
                    SaveConversation();
                },
                delegate (string error)
                {
                    AppendTranscript("\n⚠ " + error + "\n");
                    _sendButton.SetEnabled(true);
                    SetStatus("失败");
                    SaveConversation();
                });
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

        /// <summary>没有配置 LLM 时的本地降级：直接输出规则引擎的结论。</summary>
        string ScriptOnlyAnswer(string question)
        {
            var snap = PerfSession.Current;
            if (snap == null) return "当前没有快照。请先点「跟随采集」（进 Play 自己操作），或从左上列表载入已有快照。\n";

            var sb = new StringBuilder();
            sb.Append("（未配置 LLM，以下为本地规则引擎结论，未使用任何外部服务。")
              .Append("要启用对话追问，点右上角「LLM 配置」填入 API Key。）\n\n");
            int count = 0;
            for (int i = 0; i < snap.findings.Count && count < 8; i++)
            {
                var f = snap.findings[i];
                if (f.severity == Severity.Info) continue;
                sb.Append("- [").Append(f.severity.ToUpperInvariant()).Append("] ").Append(f.title).Append('\n');
                sb.Append("  ").Append(f.recommendation).Append('\n');
                count++;
            }
            if (count == 0) sb.Append("未发现超出预算的问题。\n");
            return sb.ToString();
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
            _transcript.text = (_transcript.text ?? "") + markdown;
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

            var count = new Label("快照 " + paths.Count + " 个");
            count.style.flexGrow = 1;
            head.Add(count);

            var clear = new Button(delegate
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
            clear.text = "清空全部";
            clear.style.width = 70;
            head.Add(clear);
            _snapshotHost.Add(head);

            if (paths.Count == 0)
            {
                _snapshotHost.Add(new Label("（暂无快照）"));
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
                button.text = (isCurrent ? "▶ " : "   ") + id;
                button.style.flexGrow = 1;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
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
                del.style.width = 24;
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
            if (snap == null)
            {
                _detailHost.Add(new Label("还没有数据。先点「跟随采集」，或从左上列表载入已有快照。"));
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
                _detailHost.Add(new Label("未发现超出预算的问题。"));
                return;
            }

            for (int i = 0; i < snap.findings.Count; i++)
            {
                var f = snap.findings[i];
                var card = new VisualElement();
                card.style.marginBottom = 6;
                card.style.paddingLeft = 8;
                card.style.paddingRight = 8;
                card.style.paddingTop = 6;
                card.style.paddingBottom = 6;
                card.style.backgroundColor = new Color(0.17f, 0.18f, 0.2f);
                card.style.borderLeftWidth = 4;
                card.style.borderLeftColor = SeverityColor(f.severity);

                var header = new Label("[" + SeverityLabel(f.severity) + "] " + f.title);
                header.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.style.whiteSpace = WhiteSpace.Normal;
                card.Add(header);

                if (!string.IsNullOrEmpty(f.detail))
                {
                    var detail = new Label(f.detail);
                    detail.style.whiteSpace = WhiteSpace.Normal;
                    detail.style.color = new Color(0.78f, 0.8f, 0.84f);
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
                _detailHost.Add(new Label("还没有执行过一键修复。\n\n在「结论」标签里，每条结论下方都会给出修复计划，"
                    + "可执行步骤带「执行」按钮，点击前会弹窗列出将要修改的目标。"));
                return;
            }

            _detailHost.Add(new Label("共 " + records.Count + " 条记录（新的在下），日志：ProjectSettings/PerfAgent/FixLog.json"));

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
                _detailHost.Add(new Label("没有帧数据（只做了静态审计）。"));
                return;
            }

            _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                "均值 {0:0.##} ms · P50 {1:0.##} · P95 {2:0.##} · 峰值 {3:0.##}\n平均每帧分配 {4:0} B · GC {5} 次",
                snap.FrameTimeAvgMs(), snap.FrameTimePercentileMs(50), snap.FrameTimePercentileMs(95), snap.FrameTimeMaxMs(),
                snap.AvgManagedAllocBytesPerFrame(), snap.GcEventCount())));

            _detailHost.Add(new Label(""));
            _detailHost.Add(new Label("最慢的 20 帧："));
            var frames = new List<FrameStat>(snap.frames);
            frames.Sort(delegate (FrameStat a, FrameStat b) { return b.deltaMs.CompareTo(a.deltaMs); });
            for (int i = 0; i < frames.Count && i < 20; i++)
            {
                var f = frames[i];
                _detailHost.Add(new Label(string.Format(CultureInfo.InvariantCulture,
                    "帧 {0,-8} {1,8:0.##} ms  分配 {2,10} B  DrawCall {3,5}  TempAlloc {4:0.0} MB",
                    f.frame, f.deltaMs, f.managedAllocBytes, f.drawCalls, f.tempAllocBytes / 1048576.0)));
            }
        }

        void RenderMarkers(PerfSnapshot snap)
        {
            if (snap.markers.Count == 0)
            {
                _detailHost.Add(new Label("无 Marker 数据。可能是抓帧时 Profiler 未记录，或该版本层级视图 API 形态不同（运行 API 探针确认）。"));
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
            for (int i = 0; i < snap.assetIssues.Count && i < 200; i++)
            {
                var a = snap.assetIssues[i];
                var label = new Label("[" + SeverityLabel(a.severity) + "] " + a.path + "\n    " + a.issue + "\n    → " + a.suggestion);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginBottom = 4;
                label.style.color = SeverityColor(a.severity);
                _detailHost.Add(label);
            }
        }

        void RenderScene(PerfSnapshot snap)
        {
            for (int i = 0; i < snap.sceneIssues.Count && i < 200; i++)
            {
                var v = snap.sceneIssues[i];
                var label = new Label("[" + SeverityLabel(v.severity) + "] " + v.hierarchyPath + " (" + v.componentType + ")\n    " + v.issue + "\n    → " + v.suggestion);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginBottom = 4;
                label.style.color = SeverityColor(v.severity);
                _detailHost.Add(label);
            }
        }

        void RenderCode(PerfSnapshot snap)
        {
            for (int i = 0; i < snap.codeIssues.Count && i < 300; i++)
            {
                var c = snap.codeIssues[i];
                var label = new Label("[" + SeverityLabel(c.severity) + "] " + c.file + ":" + c.line + "  <" + c.pattern + ">\n    " + c.snippet + "\n    → " + c.suggestion);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginBottom = 4;
                label.style.color = SeverityColor(c.severity);
                _detailHost.Add(label);
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

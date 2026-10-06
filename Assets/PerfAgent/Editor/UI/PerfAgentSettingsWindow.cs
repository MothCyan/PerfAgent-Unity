using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using PerfAgent.Agent;
using PerfAgent.Core;

namespace PerfAgent.UI
{
    /// <summary>
    /// LLM 配置窗口。
    ///
    /// 定位：在性能诊断面板里发消息前能就地配好 Key，不用去 Project Settings 里翻。
    /// 数据仍然只有一份（PerfAgentSettings 资产 + EditorPrefs），所以和 Project Settings 页不会分叉。
    ///
    /// 安全约定：
    ///  1. API Key 只写进本机 EditorPrefs，**不写进工程目录**，避免被提交；
    ///  2. 界面填写优先，留空才回退到环境变量（环境变量适合 CI / 多人共用机器）；
    ///  3. 「测试连接」只发一个固定字符串 "ping"，不带 tools、不带快照、不带任何工程信息。
    /// </summary>
    public class PerfAgentSettingsWindow : EditorWindow
    {
        class Preset
        {
            public string label;
            public string endpoint;
            public string model;
            public string hint;

            public Preset(string label, string endpoint, string model, string hint)
            {
                this.label = label;
                this.endpoint = endpoint;
                this.model = model;
                this.hint = hint;
            }
        }

        /// <summary>都是 OpenAI 兼容端点。预设只是省打字，能否用通请点「测试连接」确认。</summary>
        static readonly Preset[] Presets =
        {
            new Preset("OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", "官方云端，需要梯子"),
            new Preset("DeepSeek", "https://api.deepseek.com/v1/chat/completions", "deepseek-flash", "国内直连，性价比高（V4.1-Flash，支持视觉，1M 上下文）"),
            new Preset("DeepSeek Pro", "https://api.deepseek.com/v1/chat/completions", "deepseek-v4-pro", "更强推理，不支持视觉"),
            new Preset("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4-flash", "国内直连，有免费额度"),
            new Preset("阿里通义", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen-plus", "国内直连"),
            new Preset("Ollama 本地", "http://localhost:11434/v1/chat/completions", "qwen2.5:7b", "本机推理，不需要 Key、不发外网"),
            new Preset("自定义", "", "", "自建网关 / Azure / 其它兼容服务"),
        };

        Label _status;
        TextField _endpoint;
        TextField _model;
        TextField _key;
        Toggle _showKey;
        Label _keySource;
        Slider _temperature;
        IntegerField _maxSteps;
        IntegerField _maxTokens;

        /// <summary>「最大输出 tokens」现场诊断与一键修好的容器（推理型模型把预算吃完时正文一个字都没有）。</summary>
        VisualElement _maxTokensHintHost;
        Toggle _localOnly;
        Toggle _allowSource;
        Toggle _allowPaths;
        Button _checkButton;
        Label _checkSummary;
        Label _checkEndpointRow;
        Label _checkKeyRow;
        Label _checkModelRow;
        Label _checkNetworkRow;
        Label _keyPresence;
        Label _endpointHint;

        [MenuItem("Tools/PerfAgent/LLM 配置", false, 104)]
        public static void Open()
        {
            var window = GetWindow<PerfAgentSettingsWindow>("PerfAgent LLM 配置");
            window.minSize = new Vector2(560, 560);
            window.Show();
        }

        /// <summary>配置变更时触发，供主面板刷新 LLM 状态条，避免两处状态不一致。</summary>
        public static event Action Changed;

        static void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>供外部（主面板的「启用 AI」开关）在改动配置后通知监听者。</summary>
        public static void NotifyExternalChange()
        {
            NotifyChanged();
        }

        /// <summary>
        /// 「最大输出 tokens」的现场诊断 + 一键修好。
        ///
        /// 为什么要做成诊断而不是一句「建议调大」：推理型模型把输出预算先花在 reasoning_content 上，
        /// 值太小时正文一个字都轮不到（实测反馈：推理 5479 字、正文 0 字、finish_reason=length，
        /// 当时这个值是 1500）—— 而**旧工程的设置是持久化的**，
        /// 光改代码里的默认值（已提到 4096）救不了他，得在这里当场把值改掉并保存。
        /// </summary>
        void RefreshMaxTokensHint(PerfAgentSettings cfg)
        {
            if (_maxTokensHintHost == null || cfg == null) return;
            _maxTokensHintHost.Clear();

            int value = cfg.maxOutputTokens;
            bool fine = value >= 4096;

            var label = new Label(fine
                ? ("当前 " + value + "：够推理型模型先写完推理、再写正文。")
                : ("当前 " + value + " 偏小：推理型模型（如 deepseek-reasoner）会把预算先花在推理上，"
                   + "正文可能一个字都轮不到 —— 表现是「推理几千字、正文 0 字，finish_reason=length」。建议 ≥4096。"));
            label.style.fontSize = Theme.SizeSmall;
            label.style.color = fine ? Theme.TextDim : Theme.Warn;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 4;
            _maxTokensHintHost.Add(label);

            if (fine) return;

            var fix = Theme.Ghost("设为 4096", delegate
            {
                cfg.maxOutputTokens = 4096;
                cfg.Save();
                if (_maxTokens != null) _maxTokens.value = 4096;
                RefreshMaxTokensHint(cfg);
                NotifyChanged();
                SetStatus("已把「最大输出 tokens」设为 4096 并保存：推理型模型就有额度写正文了。", Dim());
            });
            fix.tooltip = "把「最大输出 tokens」设成 4096 并保存 —— 给推理型模型留出写正文的额度。";
            _maxTokensHintHost.Add(fix);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = Theme.WindowBg;
            Theme.Pad(root, 10, 10, 8, 8);

            var cfg = PerfAgentSettings.Config;

            // ---- 顶部：标题 + 状态胶囊 ----
            var header = Theme.Header("LLM 配置",
                "只有「对话追问」会用到这里；规则引擎的结论不依赖任何外部服务，没配也能用");
            _status = Theme.Pill("就绪", Theme.TextDim);
            header.Add(_status);
            root.Add(header);
            root.Add(Theme.Divider());

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            // ---- 服务商预设 ----
            var presetCard = Theme.Card("服务商预设", "只是省打字，能不能用通点下面的「开始校验」");
            var presetRow = new VisualElement();
            presetRow.style.flexDirection = FlexDirection.Row;
            presetRow.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < Presets.Length; i++)
            {
                var preset = Presets[i];
                var button = Theme.Secondary(preset.label, delegate { ApplyPreset(preset); });
                button.tooltip = preset.hint;
                presetRow.Add(button);
            }
            presetCard.Add(presetRow);
            scroll.Add(presetCard);

            // ---- 连接 ----
            var connCard = Theme.Card("连接");

            _endpoint = new TextField("Endpoint");
            _endpoint.value = cfg.endpoint;
            _endpoint.style.fontSize = Theme.SizeSmall;
            _endpoint.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                cfg.endpoint = e.newValue;
                RefreshEndpointHint();
            });
            connCard.Add(_endpoint);

            // 把实际要请求的 URL 显示出来：用户只填域名时能一眼看到补全结果
            _endpointHint = Theme.Hint("");
            connCard.Add(_endpointHint);

            _model = new TextField("模型");
            _model.value = cfg.model;
            _model.style.fontSize = Theme.SizeSmall;
            _model.style.marginTop = 4;
            _model.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { cfg.model = e.newValue; });
            connCard.Add(_model);

            var keyRow = new VisualElement();
            keyRow.style.flexDirection = FlexDirection.Row;
            keyRow.style.alignItems = Align.Center;
            keyRow.style.marginTop = 4;

            _key = new TextField("API Key");
            // 只回显本机存的那个：环境变量里的 Key 不应该被显示到界面上
            _key.value = cfg.StoredApiKey;
            _key.isPasswordField = true;
            _key.style.flexGrow = 1;
            _key.style.fontSize = Theme.SizeSmall;
            _key.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                cfg.ApiKey = e.newValue;
                RefreshKeyState();
                NotifyChanged();
            });
            keyRow.Add(_key);

            _showKey = new Toggle("显示明文");
            _showKey.value = false;
            _showKey.style.width = 86;
            _showKey.style.marginLeft = 6;
            _showKey.style.fontSize = Theme.SizeSmall;
            _showKey.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e)
            {
                if (_key != null) _key.isPasswordField = !e.newValue;
            });
            keyRow.Add(_showKey);
            connCard.Add(keyRow);

            _keySource = Theme.Hint("");
            connCard.Add(_keySource);

            _keyPresence = new Label();
            _keyPresence.style.fontSize = Theme.SizeSmall;
            _keyPresence.style.whiteSpace = WhiteSpace.Normal;
            connCard.Add(_keyPresence);

            connCard.Add(Theme.Hint("安全：Key 只写本机 EditorPrefs（不进工程目录）；"
                + "当前的保护实现是「" + SecretProtection.Describe() + "」。"));
            scroll.Add(connCard);

            // ---- 连通性校验 ----
            var checkCard = Theme.Card("连通性校验", "只发一个固定字符串 \"ping\"，不带 tools / 快照 / 任何工程信息");
            var checkActions = new VisualElement();
            checkActions.style.flexDirection = FlexDirection.Row;
            checkActions.style.alignItems = Align.Center;

            _checkButton = Theme.Primary("开始校验", RunConnectivityCheck);
            _checkButton.style.marginBottom = 0;
            checkActions.Add(_checkButton);

            _checkSummary = Theme.Hint("还没校验过。");
            _checkSummary.style.marginTop = 0;
            _checkSummary.style.marginLeft = 8;
            _checkSummary.style.flexGrow = 1;
            _checkSummary.style.flexShrink = 1;
            checkActions.Add(_checkSummary);
            checkCard.Add(checkActions);

            checkCard.Add(Theme.Divider());

            _checkEndpointRow = CheckRow();
            _checkKeyRow = CheckRow();
            _checkModelRow = CheckRow();
            _checkNetworkRow = CheckRow();
            checkCard.Add(_checkEndpointRow);
            checkCard.Add(_checkKeyRow);
            checkCard.Add(_checkModelRow);
            checkCard.Add(_checkNetworkRow);
            scroll.Add(checkCard);

            // ---- 参数 ----
            var paramCard = Theme.Card("参数", "影响成本与回答风格");

            _temperature = new Slider("Temperature", 0f, 1f);
            _temperature.value = cfg.temperature;
            _temperature.showInputField = true;
            _temperature.style.fontSize = Theme.SizeSmall;
            _temperature.RegisterValueChangedCallback(delegate (ChangeEvent<float> e) { cfg.temperature = e.newValue; });
            paramCard.Add(_temperature);

            _maxSteps = new IntegerField();
            _maxSteps.value = cfg.maxSteps;
            _maxSteps.style.fontSize = Theme.SizeSmall;
            _maxSteps.RegisterValueChangedCallback(delegate (ChangeEvent<int> e) { cfg.maxSteps = Mathf.Clamp(e.newValue, 1, 20); });
            paramCard.Add(Theme.FormRow("工具最大轮数", _maxSteps));

            _maxTokens = new IntegerField();
            _maxTokens.value = cfg.maxOutputTokens;
            _maxTokens.style.fontSize = Theme.SizeSmall;
            _maxTokens.RegisterValueChangedCallback(delegate (ChangeEvent<int> e)
            {
                cfg.maxOutputTokens = Mathf.Max(64, e.newValue);
                RefreshMaxTokensHint(cfg);
            });
            paramCard.Add(Theme.FormRow("最大输出 tokens", _maxTokens));

            // 推理型模型会把预算先花在 reasoning_content 上：值太小时正文一个字都轮不到
            //（实测反馈：模型把输出预算全用在推理上了，正文 0 字；当时这个值是 1500）。
            // 旧工程里存的是老默认值，所以这里给一句现场诊断 + 一键修好，而不是让他去猜该填多少。
            _maxTokensHintHost = new VisualElement();
            paramCard.Add(_maxTokensHintHost);
            RefreshMaxTokensHint(cfg);
            scroll.Add(paramCard);

            // ---- 隐私 ----
            var privacyCard = Theme.Card("隐私", "三个开关都是「关掉更安全」的方向");
            _localOnly = new Toggle("纯本地模式（完全不调用外部服务）");
            _localOnly.value = cfg.localOnlyNoLlm;
            _localOnly.style.fontSize = Theme.SizeSmall;
            _localOnly.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e)
            {
                cfg.localOnlyNoLlm = e.newValue;
                RefreshKeyState();
                NotifyChanged();
            });
            privacyCard.Add(_localOnly);

            _allowSource = new Toggle("允许上传代码片段");
            _allowSource.value = cfg.allowSourceCodeUpload;
            _allowSource.style.fontSize = Theme.SizeSmall;
            _allowSource.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.allowSourceCodeUpload = e.newValue; });
            privacyCard.Add(_allowSource);

            _allowPaths = new Toggle("允许上传资源路径");
            _allowPaths.value = cfg.allowAssetPathUpload;
            _allowPaths.style.fontSize = Theme.SizeSmall;
            _allowPaths.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.allowAssetPathUpload = e.newValue; });
            privacyCard.Add(_allowPaths);

            privacyCard.Add(Theme.Hint("关闭「允许上传代码片段」后，Agent 只能看到反模式名称与文件:行号，看不到源码内容。"));
            scroll.Add(privacyCard);

            // ---- 采集 ----
            var captureCard = Theme.Card("采集", "点一下就把活儿干完，少一步手动操作");

            var autoPlay = new Toggle("点「跟随采集」后自动进入 Play");
            autoPlay.value = cfg.autoPlayOnFollowCapture;
            autoPlay.style.fontSize = Theme.SizeSmall;
            autoPlay.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.autoPlayOnFollowCapture = e.newValue; });
            captureCard.Add(autoPlay);

            var autoAnalyze = new Toggle("采集结束后自动分析（出结论）");
            autoAnalyze.value = cfg.autoAnalyzeAfterCapture;
            autoAnalyze.style.fontSize = Theme.SizeSmall;
            autoAnalyze.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.autoAnalyzeAfterCapture = e.newValue; });
            captureCard.Add(autoAnalyze);

            captureCard.Add(Theme.Hint("自动进 Play 之前会先量一次「编辑器开销基线」（约 1~4 秒，只能在编辑模式量）；"
                + "量完替你按 Play 并开始记录。想先做好准备再手动进 Play，就把第一个开关关掉。"));
            scroll.Add(captureCard);

            // ---- 操作 ----
            var actionCard = Theme.Card("操作");
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;

            actions.Add(Theme.Primary("保存", delegate
            {
                PerfAgentSettings.Config.Save();
                NotifyChanged();
                SetStatus("已保存 · " + PerfAgentSettings.Config.Describe(), Theme.Good);
            }));
            actions.Add(Theme.Secondary("打开 Project Settings", delegate { SettingsService.OpenProjectSettings("Project/PerfAgent"); }));
            actions.Add(Theme.Ghost("API 探针", delegate { PerfApiProbeWindow.Open(); }));
            actionCard.Add(actions);
            actionCard.Add(Theme.Hint("· API Key 只写在本机 EditorPrefs，不进工程目录，不会被提交。\n"
                + "· 也可用环境变量 PERF_AGENT_API_KEY，优先级更高（设置后上面输入框的值会被忽略）。\n"
                + "· 校验只发送固定字符串 \"ping\"，不带 tools、不带快照、不带任何工程信息。"));
            scroll.Add(actionCard);

            RefreshEndpointHint();
            RefreshKeyState();
        }

        /// <summary>
        /// 窗口重新获得焦点时把控件同步回配置。
        /// 主面板顶部也有一个「启用 AI」开关，改的是同一份配置 —— 不同步的话这里显示的就是旧状态。
        /// </summary>
        void OnFocus()
        {
            var cfg = PerfAgentSettings.Config;
            if (_localOnly != null) _localOnly.SetValueWithoutNotify(cfg.localOnlyNoLlm);
            if (_endpoint != null) _endpoint.SetValueWithoutNotify(cfg.endpoint);
            if (_model != null) _model.SetValueWithoutNotify(cfg.model);
            RefreshEndpointHint();
            RefreshKeyState();
        }

        void ApplyPreset(Preset preset)
        {
            if (string.IsNullOrEmpty(preset.endpoint))
            {
                SetStatus("已切到「自定义」：请自行填写 Endpoint 与模型名。", Dim());
                return;
            }

            var cfg = PerfAgentSettings.Config;
            cfg.endpoint = preset.endpoint;
            cfg.model = preset.model;
            cfg.Save();

            if (_endpoint != null) _endpoint.value = preset.endpoint;
            if (_model != null) _model.value = preset.model;

            NotifyChanged();
            SetStatus("已填入 " + preset.label + " 的 Endpoint 与模型（" + preset.hint + "）。填好 API Key 后点上面的「开始校验」。", Dim());
            RefreshKeyState();
        }

        void RefreshEndpointHint()
        {
            if (_endpointHint == null) return;
            var cfg = PerfAgentSettings.Config;

            if (string.IsNullOrWhiteSpace(cfg.endpoint))
            {
                _endpointHint.text = "↑ 填完整地址，例如 https://api.deepseek.com/v1/chat/completions";
                _endpointHint.style.color = Warn();
                return;
            }

            string entered = cfg.endpoint.Trim().TrimEnd('/');
            string effective = LlmClient.NormalizeEndpoint(cfg.endpoint);
            if (!string.Equals(effective, entered, StringComparison.OrdinalIgnoreCase))
            {
                _endpointHint.text = "实际请求：" + effective + "（已自动补全路径）";
                _endpointHint.style.color = Ok();
            }
            else
            {
                _endpointHint.text = "实际请求：" + effective;
                _endpointHint.style.color = Dim();
            }
        }

        void RefreshKeyState()
        {
            var cfg = PerfAgentSettings.Config;
            bool hasKey = cfg.HasApiKey;

            if (_keySource != null)
            {
                _keySource.text = string.IsNullOrEmpty(cfg.ApiKeySource)
                    ? "当前 Key：未设置。界面填写优先；留空则回退到环境变量（PERF_AGENT_API_KEY / DEEPSEEK_API_KEY / OPENAI_API_KEY）。"
                    : "当前 Key 来源：" + cfg.ApiKeySource + "。界面填写优先，把输入框清空即可回退到环境变量。";
                _keySource.style.color = hasKey ? Ok() : Dim();
            }

            if (_keyPresence != null)
            {
                bool local = LlmClient.IsLocalEndpoint(cfg.endpoint);
                if (cfg.localOnlyNoLlm)
                    _keyPresence.text = "纯本地模式已开启：不会调用任何外部服务，无需 API Key。";
                else if (hasKey)
                    _keyPresence.text = "状态：API Key 已配置（" + cfg.ApiKeySource + "）。";
                else if (local)
                    _keyPresence.text = "状态：Endpoint 指向本机服务，不需要 API Key。";
                else
                    _keyPresence.text = "状态：未配置 API Key —— 仍可使用，Agent 会退化为本地规则引擎结论。";

                _keyPresence.style.color = (hasKey || local || cfg.localOnlyNoLlm) ? Ok() : Warn();
            }
        }

        static Label CheckRow()
        {
            var l = new Label("• 未校验");
            l.style.fontSize = Theme.SizeSmall;
            l.style.color = Theme.TextFaint;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginBottom = 2;
            return l;
        }

        /// <summary>0 = 未校验，1 = 通过，2 = 失败。</summary>
        static void SetCheck(Label row, int state, string text)
        {
            if (row == null) return;
            row.text = (state == 1 ? "✓ " : state == 2 ? "✗ " : "• ") + text;
            row.style.color = state == 1 ? Theme.Good : state == 2 ? Theme.Bad : Theme.TextFaint;
        }

        /// <summary>把失败归类翻译成人话 —— 只报一个 HTTP 码对排查没帮助。</summary>
        static string Kind(string kind)
        {
            switch (kind)
            {
                case "endpoint": return "Endpoint 配置";
                case "key": return "缺少 API Key";
                case "model": return "模型名配置";
                case "busy": return "已有请求在进行";
                case "timeout": return "超时";
                case "auth": return "鉴权失败";
                case "notfound": return "路径不存在";
                case "request": return "请求被拒";
                case "server": return "服务端错误";
                case "network": return "网络不可达";
                default: return "未知原因";
            }
        }

        /// <summary>
        /// 连通性校验：前四项里前三项本地就能判定（不花网络请求），最后一项才真的发一次 ping。
        /// 逐项显示的意义是「卡在哪一步一目了然」—— 以前只有一句「连接成功/失败」，
        /// 失败时用户不知道是路径错、Key 错还是模型名错。
        /// </summary>
        void RunConnectivityCheck()
        {
            var cfg = PerfAgentSettings.Config;

            string effective = LlmClient.NormalizeEndpoint(cfg.endpoint);
            bool endpointOk = !string.IsNullOrEmpty(cfg.endpoint) && !string.IsNullOrEmpty(effective);
            SetCheck(_checkEndpointRow, endpointOk ? 1 : 2, endpointOk
                ? ("Endpoint 解析为 " + effective)
                : "Endpoint 未填写或无法解析");

            bool local = LlmClient.IsLocalEndpoint(cfg.endpoint);
            bool keyOk = cfg.HasApiKey || local;
            SetCheck(_checkKeyRow, keyOk ? 1 : 2, cfg.HasApiKey
                ? ("API Key 已就绪（来源：" + cfg.ApiKeySource + "）")
                : (local ? "Endpoint 指向本机服务，不需要 API Key" : "未配置 API Key"));

            bool modelOk = !string.IsNullOrEmpty(cfg.model);
            SetCheck(_checkModelRow, modelOk ? 1 : 2, modelOk ? ("模型名：" + cfg.model) : "未填写模型名");

            if (cfg.localOnlyNoLlm)
            {
                SetCheck(_checkNetworkRow, 0, "当前是「纯本地模式」，不会发起网络请求；要校验连通性先关掉它。");
                _checkSummary.text = "纯本地模式下没有可校验的连接。";
                _checkSummary.style.color = Theme.TextDim;
                return;
            }

            if (!endpointOk || !keyOk || !modelOk)
            {
                SetCheck(_checkNetworkRow, 2, "上面有没过的项，先补齐再校验网络。");
                _checkSummary.text = "配置不完整，未发起请求。";
                _checkSummary.style.color = Theme.Bad;
                return;
            }

            if (_checkButton != null) _checkButton.SetEnabled(false);
            SetCheck(_checkNetworkRow, 0, "正在请求…");
            _checkSummary.text = "校验中…";
            _checkSummary.style.color = Theme.TextDim;

            LlmClient.PingEx(delegate (LlmClient.PingResult r)
            {
                if (_checkButton != null) _checkButton.SetEnabled(true);

                SetCheck(_checkNetworkRow, r.ok ? 1 : 2, r.ok
                    ? ("服务端可达（HTTP " + r.httpCode + "，" + r.elapsedMs.ToString("0") + " ms）"
                       + (string.IsNullOrEmpty(r.serverModel) ? "" : "，回传模型 " + r.serverModel))
                    : ("请求失败（" + Kind(r.errorKind) + "，" + r.elapsedMs.ToString("0") + " ms）\n" + r.message));

                _checkSummary.text = (r.ok ? "✓ 连通正常" : "✗ " + Kind(r.errorKind))
                    + "　·　" + DateTime.Now.ToString("HH:mm:ss")
                    + "　·　" + r.elapsedMs.ToString("0") + " ms"
                    + (string.IsNullOrEmpty(r.effectiveUrl) ? "" : "　·　" + r.effectiveUrl);
                _checkSummary.style.color = r.ok ? Theme.Good : Theme.Bad;
            });
        }

        void SetStatus(string text, Color color)
        {
            if (_status == null) return;
            _status.text = text;
            // 胶囊的底色是按文字色算出来的，所以只能走 TintPill —— 否则出现绿字红底
            Theme.TintPill(_status, color);
        }

        static Color Ok() { return Theme.Good; }
        static Color Warn() { return Theme.Warn; }
        static Color Dim() { return Theme.TextDim; }
        static Color Bad() { return Theme.Bad; }
    }
}

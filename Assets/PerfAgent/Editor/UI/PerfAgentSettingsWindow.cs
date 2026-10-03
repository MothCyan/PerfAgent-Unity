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
        Toggle _localOnly;
        Toggle _allowSource;
        Toggle _allowPaths;
        Button _testButton;
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

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            root.Add(Section("服务商预设（只是省打字，能否用通请用「测试连接」确认）"));
            var presetRow = new VisualElement();
            presetRow.style.flexDirection = FlexDirection.Row;
            presetRow.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < Presets.Length; i++)
            {
                var preset = Presets[i];
                var button = new Button(delegate { ApplyPreset(preset); });
                button.text = preset.label;
                button.tooltip = preset.hint;
                button.style.marginRight = 4;
                button.style.marginBottom = 4;
                presetRow.Add(button);
            }
            root.Add(presetRow);

            var cfg = PerfAgentSettings.Config;

            root.Add(Section("连接"));
            _endpoint = new TextField("Endpoint");
            _endpoint.value = cfg.endpoint;
            _endpoint.style.marginBottom = 2;
            _endpoint.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                cfg.endpoint = e.newValue;
                RefreshEndpointHint();
            });
            root.Add(_endpoint);

            // 把实际要请求的 URL 显示出来：用户只填域名时能一眼看到补全结果
            _endpointHint = new Label();
            _endpointHint.style.fontSize = 10;
            _endpointHint.style.marginBottom = 6;
            root.Add(_endpointHint);

            _model = new TextField("模型");
            _model.value = cfg.model;
            _model.style.marginBottom = 2;
            _model.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { cfg.model = e.newValue; });
            root.Add(_model);

            var keyRow = new VisualElement();
            keyRow.style.flexDirection = FlexDirection.Row;
            keyRow.style.alignItems = Align.Center;

            _key = new TextField("API Key");
            // 只回显本机存的那个：环境变量里的 Key 不应该被显示到界面上
            _key.value = cfg.StoredApiKey;
            _key.isPasswordField = true;
            _key.style.flexGrow = 1;
            _key.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                cfg.ApiKey = e.newValue;
                RefreshKeyState();
                NotifyChanged();
            });
            keyRow.Add(_key);

            _showKey = new Toggle("显示明文");
            _showKey.value = false;
            _showKey.style.width = 90;
            _showKey.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e)
            {
                if (_key != null) _key.isPasswordField = !e.newValue;
            });
            keyRow.Add(_showKey);
            root.Add(keyRow);

            _keySource = new Label();
            _keySource.style.fontSize = 11;
            _keySource.style.whiteSpace = WhiteSpace.Normal;
            _keySource.style.marginBottom = 2;
            root.Add(_keySource);

            _keyPresence = new Label();
            _keyPresence.style.fontSize = 11;
            _keyPresence.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_keyPresence);

            root.Add(Section("参数"));
            _temperature = new Slider("Temperature", 0f, 1f);
            _temperature.value = cfg.temperature;
            _temperature.showInputField = true;
            _temperature.RegisterValueChangedCallback(delegate (ChangeEvent<float> e) { cfg.temperature = e.newValue; });
            root.Add(_temperature);

            var paramRow = new VisualElement();
            paramRow.style.flexDirection = FlexDirection.Row;

            _maxSteps = new IntegerField("工具最大轮数");
            _maxSteps.value = cfg.maxSteps;
            _maxSteps.style.flexGrow = 1;
            _maxSteps.RegisterValueChangedCallback(delegate (ChangeEvent<int> e) { cfg.maxSteps = Mathf.Clamp(e.newValue, 1, 20); });
            paramRow.Add(_maxSteps);

            _maxTokens = new IntegerField("最大输出 tokens");
            _maxTokens.value = cfg.maxOutputTokens;
            _maxTokens.style.flexGrow = 1;
            _maxTokens.style.marginLeft = 8;
            _maxTokens.RegisterValueChangedCallback(delegate (ChangeEvent<int> e) { cfg.maxOutputTokens = Mathf.Max(64, e.newValue); });
            paramRow.Add(_maxTokens);
            root.Add(paramRow);

            root.Add(Section("隐私"));
            _localOnly = new Toggle("纯本地模式（完全不调用外部服务）");
            _localOnly.value = cfg.localOnlyNoLlm;
            _localOnly.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.localOnlyNoLlm = e.newValue; RefreshKeyState(); NotifyChanged(); });
            root.Add(_localOnly);

            _allowSource = new Toggle("允许上传代码片段");
            _allowSource.value = cfg.allowSourceCodeUpload;
            _allowSource.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.allowSourceCodeUpload = e.newValue; });
            root.Add(_allowSource);

            _allowPaths = new Toggle("允许上传资源路径");
            _allowPaths.value = cfg.allowAssetPathUpload;
            _allowPaths.RegisterValueChangedCallback(delegate (ChangeEvent<bool> e) { cfg.allowAssetPathUpload = e.newValue; });
            root.Add(_allowPaths);

            var privacyNote = new Label("关闭「允许上传代码片段」后，Agent 只能看到反模式名称与文件:行号，看不到源码内容。");
            privacyNote.style.fontSize = 11;
            privacyNote.style.whiteSpace = WhiteSpace.Normal;
            privacyNote.style.color = Dim();
            root.Add(privacyNote);

            root.Add(Section("操作"));
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;

            _testButton = new Button(TestConnection);
            _testButton.text = "测试连接";
            _testButton.style.marginRight = 6;
            actions.Add(_testButton);

            var save = new Button(delegate
            {
                PerfAgentSettings.Config.Save();
                NotifyChanged();
                SetStatus("已保存。当前：" + PerfAgentSettings.Config.Describe(), Ok());
            });
            save.text = "保存";
            save.style.marginRight = 6;
            actions.Add(save);

            var settings = new Button(delegate { SettingsService.OpenProjectSettings("Project/PerfAgent"); });
            settings.text = "打开 Project Settings";
            settings.style.marginRight = 6;
            actions.Add(settings);

            var probe = new Button(delegate { PerfApiProbeWindow.Open(); });
            probe.text = "API 探针";
            actions.Add(probe);

            root.Add(actions);

            _status = new Label("就绪");
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 6;
            root.Add(_status);

            var note = new Label(
                "· API Key 只写在本机 EditorPrefs，不进工程目录，不会被提交。\n" +
                "· 也可用环境变量 PERF_AGENT_API_KEY，优先级更高（设置后下面输入框的值会被忽略）。\n" +
                "· 「测试连接」只发送固定字符串 \"ping\"，不带 tools、不带快照、不带任何工程信息。");
            note.style.fontSize = 11;
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.color = Dim();
            note.style.marginTop = 8;
            root.Add(note);

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
            SetStatus("已填入 " + preset.label + " 的 Endpoint 与模型（" + preset.hint + "）。填好 API Key 后点「测试连接」。", Dim());
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

        void TestConnection()
        {
            if (_testButton != null) _testButton.SetEnabled(false);
            SetStatus("正在测试连接（只发一个 \"ping\"，不带任何工程数据）…", Dim());

            LlmClient.Ping(delegate (bool ok, string message)
            {
                if (_testButton != null) _testButton.SetEnabled(true);
                SetStatus((ok ? "✓ " : "✗ ") + message, ok ? Ok() : Warn());
            });
        }

        void SetStatus(string text, Color color)
        {
            if (_status == null) return;
            _status.text = text;
            _status.style.color = color;
            _status.style.whiteSpace = WhiteSpace.Normal;
        }

        static Label Section(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 10;
            label.style.marginBottom = 3;
            label.style.color = new Color(0.62f, 0.72f, 0.9f);
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        static Color Ok() { return new Color(0.55f, 0.87f, 0.62f); }
        static Color Warn() { return new Color(1f, 0.83f, 0.48f); }
        static Color Dim() { return new Color(0.62f, 0.66f, 0.72f); }
    }
}

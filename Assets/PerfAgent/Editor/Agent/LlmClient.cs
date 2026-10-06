using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Agent
{
    /// <summary>
    /// OpenAI 兼容协议的 LLM 客户端。
    ///
    /// 使用 UnityWebRequest + DownloadHandlerScript 做流式接收，不引入任何第三方依赖，
    /// 也不需要 async/await（旧版本 Unity 上 Awaitable 不可用）。
    /// 同时兼容非流式响应（服务端忽略 stream 参数时自动回退）。
    /// </summary>
    public static class LlmClient
    {
        public const double TimeoutSeconds = 180.0;

        public static bool Busy { get; private set; }

        public static void Send(List<object> messages,
            Action<string> onDelta,
            Action<Dictionary<string, object>> onMessage,
            Action<string> onError)
        {
            var cfg = PerfAgentSettings.Config;
            if (string.IsNullOrEmpty(cfg.endpoint))
            {
                onError("未配置 LLM Endpoint。请在「Tools > PerfAgent > LLM 配置」中填写。");
                return;
            }
            if (!cfg.HasApiKey && !IsLocalEndpoint(cfg.endpoint))
            {
                onError("未配置 API Key。可在「Tools > PerfAgent > LLM 配置」里填写，或设置环境变量 PERF_AGENT_API_KEY。");
                return;
            }
            if (Busy)
            {
                onError("已有请求正在进行中。");
                return;
            }

            var body = new Dictionary<string, object>();
            body["model"] = cfg.model;
            body["messages"] = messages;
            body["temperature"] = cfg.temperature;
            body["max_tokens"] = cfg.maxOutputTokens;
            body["stream"] = true;
            body["tools"] = ToolRegistry.Definitions();
            body["tool_choice"] = "auto";

            SendRaw(MiniJson.Serialize(body), onDelta, onMessage, onError);
        }

        static void SendRaw(string jsonBody, Action<string> onDelta,
            Action<Dictionary<string, object>> onMessage, Action<string> onError)
        {
            var cfg = PerfAgentSettings.Config;
            Busy = true;

            var handler = new SseHandler(onDelta);
            var request = new UnityWebRequest(NormalizeEndpoint(cfg.endpoint), "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = handler;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "text/event-stream");
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                request.SetRequestHeader("Authorization", "Bearer " + cfg.ApiKey);
            request.timeout = (int)TimeoutSeconds;

            double started = EditorApplication.timeSinceStartup;
            // EditorApplication.update 的委托类型是 CallbackFunction，与 Action 不兼容，必须用具体类型
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                bool finished = request.isDone;
                bool timedOut = EditorApplication.timeSinceStartup - started > TimeoutSeconds + 5;

                if (!finished && !timedOut) return;

                EditorApplication.update -= tick;
                Busy = false;

                try
                {
                    if (timedOut && !finished)
                    {
                        try { request.Abort(); } catch { }
                        onError("请求超时（" + TimeoutSeconds + "s）。");
                        return;
                    }

                    bool httpOk;
#if UNITY_2020_2_OR_NEWER
                    httpOk = request.result == UnityWebRequest.Result.Success;
#else
                    httpOk = !request.isNetworkError && !request.isHttpError;
#endif
                    var message = handler.BuildMessage();

                    if (!httpOk)
                    {
                        string raw = handler.RawText;
                        if (raw.Length > 600) raw = raw.Substring(0, 600) + "...";
                        onError("LLM 请求失败: HTTP " + request.responseCode + " " + request.error + "\n" + raw);
                        return;
                    }

                    if (message == null)
                    {
                        onError("LLM 返回内容无法解析。原始响应前 400 字符：\n" + Head(handler.RawText, 400));
                        return;
                    }

                    onMessage(message);
                }
                finally
                {
                    try { request.Dispose(); } catch { }
                }
            };

            request.SendWebRequest();
            EditorApplication.update += tick;
        }

        static string Head(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        /// <summary>
        /// 把 Endpoint 规范成能直接 POST 的完整 URL。
        /// 规则与理由见 EndpointUrl.Normalize（那段逻辑不依赖 Unity，便于回归测试）。
        /// </summary>
        public static string NormalizeEndpoint(string endpoint)
        {
            return EndpointUrl.Normalize(endpoint);
        }

        /// <summary>是否指向本机服务（Ollama / LM Studio 等本地推理不需要 API Key）。</summary>
        public static bool IsLocalEndpoint(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return false;
            string e = endpoint.ToLowerInvariant();
            return e.Contains("localhost") || e.Contains("127.0.0.1")
                || e.Contains("0.0.0.0") || e.Contains("[::1]");
        }

        /// <summary>
        /// 连通性校验的结果。比「bool + 一句话」多带上下文，界面才能逐项显示
        /// （哪一步过了、哪一步没过、卡在鉴权还是网络），而不是只丢一句「失败」。
        /// </summary>
        public class PingResult
        {
            public bool ok;
            public string message = "";
            /// <summary>耗时（毫秒）。</summary>
            public double elapsedMs;
            /// <summary>HTTP 状态码；0 = 根本没连上（DNS / TCP / 超时）。</summary>
            public int httpCode;
            /// <summary>服务端回传的模型名 —— 有它才能确认「模型名也填对了」。</summary>
            public string serverModel = "";
            /// <summary>实际请求的 URL（补全路径之后）。</summary>
            public string effectiveUrl = "";
            /// <summary>失败归类：endpoint / key / model / busy / network / timeout / auth / notfound / request / server。</summary>
            public string errorKind = "";
        }

        /// <summary>
        /// 连通性自检（旧签名，保留给已有调用方）。
        /// </summary>
        public static void Ping(Action<bool, string> done)
        {
            PingEx(delegate (PingResult r) { done(r.ok, r.message); });
        }

        /// <summary>
        /// 连通性自检（供配置窗口的「连通性校验」用）。
        ///
        /// 刻意只发一个固定字符串 "ping"：不带 tools、不带快照、不带任何工程信息。
        /// 目的只是确认 Endpoint / Key / 模型名能通，不是一个会泄露数据的请求。
        /// </summary>
        public static void PingEx(Action<PingResult> done)
        {
            var result = new PingResult();
            var cfg = PerfAgentSettings.Config;

            if (string.IsNullOrEmpty(cfg.endpoint))
            {
                result.errorKind = "endpoint";
                result.message = "未填写 Endpoint。";
                done(result);
                return;
            }
            if (string.IsNullOrEmpty(cfg.model))
            {
                result.errorKind = "model";
                result.message = "未填写模型名。";
                done(result);
                return;
            }
            if (!cfg.HasApiKey && !IsLocalEndpoint(cfg.endpoint))
            {
                result.errorKind = "key";
                result.message = "未填写 API Key（若为本机推理服务则不需要）。";
                done(result);
                return;
            }
            if (Busy)
            {
                result.errorKind = "busy";
                result.message = "已有请求正在进行，等它结束再试。";
                done(result);
                return;
            }

            var body = new Dictionary<string, object>();
            body["model"] = cfg.model;
            var messages = new List<object>();
            var ping = new Dictionary<string, object>();
            ping["role"] = "user";
            ping["content"] = "ping";
            messages.Add(ping);
            body["messages"] = messages;
            body["max_tokens"] = 8;
            body["stream"] = false;

            Busy = true;
            string url = NormalizeEndpoint(cfg.endpoint);
            result.effectiveUrl = url;

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MiniJson.Serialize(body)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                request.SetRequestHeader("Authorization", "Bearer " + cfg.ApiKey);
            request.timeout = 30;

            double started = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                bool finished = request.isDone;
                bool timedOut = EditorApplication.timeSinceStartup - started > 35;
                if (!finished && !timedOut) return;

                EditorApplication.update -= tick;
                Busy = false;
                result.elapsedMs = (EditorApplication.timeSinceStartup - started) * 1000.0;

                try
                {
                    if (timedOut && !finished)
                    {
                        try { request.Abort(); } catch { }
                        result.errorKind = "timeout";
                        result.message = "超时（35s）。检查 Endpoint 是否可访问、是否需要代理。";
                        return;
                    }

                    bool httpOk;
#if UNITY_2020_2_OR_NEWER
                    httpOk = request.result == UnityWebRequest.Result.Success;
#else
                    httpOk = !request.isNetworkError && !request.isHttpError;
#endif

                    result.httpCode = (int)request.responseCode;
                    string text = "";
                    try { text = request.downloadHandler.text ?? ""; } catch { }

                    if (!httpOk)
                    {
                        // 直接报一个 HTTP 码对排查没帮助，把最常见的几种按原因翻译一下
                        var msg = new StringBuilder();
                        msg.Append("HTTP ").Append(request.responseCode).Append("  ").Append(request.error);
                        msg.Append("\n实际请求：POST ").Append(url);
                        if (request.responseCode == 404)
                        {
                            result.errorKind = "notfound";
                            msg.Append("\n\n404 = 这个路径不存在。检查 Endpoint 是否少了 /v1/chat/completions。");
                        }
                        else if (request.responseCode == 401 || request.responseCode == 403)
                        {
                            result.errorKind = "auth";
                            msg.Append("\n\n401/403 = 鉴权失败。Key 无效、已过期，或与这个服务商不匹配。");
                        }
                        else if (request.responseCode == 400)
                        {
                            result.errorKind = "request";
                            msg.Append("\n\n400 = 请求被拒。多半是模型名不存在，当前填的是「").Append(cfg.model).Append("」。");
                        }
                        else if (request.responseCode == 429)
                        {
                            result.errorKind = "request";
                            msg.Append("\n\n429 = 被限流（额度用完或请求过快），稍后再试或换 Key。");
                        }
                        else if (request.responseCode >= 500)
                        {
                            result.errorKind = "server";
                            msg.Append("\n\n5xx = 服务端出错，不是本地配置的问题，稍后重试。");
                        }
                        else
                        {
                            result.errorKind = "network";
                        }
                        msg.Append("\n\n服务端响应：").Append(Head(text, 400));
                        result.message = msg.ToString();
                        return;
                    }

                    var dict = MiniJson.ParseObjectSafe(text);
                    string model = dict == null ? "" : MiniJson.Str(dict, "model", "");
                    result.serverModel = model;
                    result.ok = true;
                    result.message = string.IsNullOrEmpty(model)
                        ? "连接成功（响应可解析，服务端未回传 model 字段）"
                        : "连接成功，服务端回传模型：" + model;
                }
                finally
                {
                    try { request.Dispose(); } catch { }
                    done(result);
                }
            };

            request.SendWebRequest();
            EditorApplication.update += tick;
        }

        /// <summary>
        /// 单轮问答：**不带工具、非流式**。
        ///
        /// 给「生成代码修复建议」这类场景用。两个取舍：
        ///   - 不带工具：它不需要 Agent 循环，带上 tools 反而会让模型去调工具而不是直接回答，
        ///     还白花几百 token 的 schema；
        ///   - 非流式：要拿到完整文本才好解析代码块，流式拼装容易在边界上出错。
        /// </summary>
        public static void Ask(string systemPrompt, string userPrompt,
            Action<string> onText, Action<string> onError)
        {
            var cfg = PerfAgentSettings.Config;
            if (string.IsNullOrEmpty(cfg.endpoint)) { onError("未配置 LLM Endpoint。"); return; }
            if (string.IsNullOrEmpty(cfg.model)) { onError("未配置模型名。"); return; }
            if (!cfg.HasApiKey && !IsLocalEndpoint(cfg.endpoint)) { onError("未配置 API Key。"); return; }
            if (Busy) { onError("已有请求正在进行，等它结束再试。"); return; }

            var messages = new List<object>();

            var sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = systemPrompt;
            messages.Add(sys);

            var user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = userPrompt;
            messages.Add(user);

            var body = new Dictionary<string, object>();
            body["model"] = cfg.model;
            body["messages"] = messages;
            body["temperature"] = 0.1;   // 改代码要稳，不要创意
            body["max_tokens"] = cfg.maxOutputTokens;
            body["stream"] = false;

            string url = NormalizeEndpoint(cfg.endpoint);

            Busy = true;
            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MiniJson.Serialize(body)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                request.SetRequestHeader("Authorization", "Bearer " + cfg.ApiKey);
            request.timeout = 120;

            double started = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                bool finished = request.isDone;
                bool timedOut = EditorApplication.timeSinceStartup - started > 125;
                if (!finished && !timedOut) return;

                EditorApplication.update -= tick;
                Busy = false;

                try
                {
                    if (timedOut && !finished)
                    {
                        try { request.Abort(); } catch { }
                        onError("请求超时（120s）。");
                        return;
                    }

                    bool httpOk;
#if UNITY_2020_2_OR_NEWER
                    httpOk = request.result == UnityWebRequest.Result.Success;
#else
                    httpOk = !request.isNetworkError && !request.isHttpError;
#endif

                    string text = "";
                    try { text = request.downloadHandler.text ?? ""; } catch { }

                    if (!httpOk)
                    {
                        onError("LLM 请求失败: HTTP " + request.responseCode + "\n" + Head(text, 400));
                        return;
                    }

                    var dict = MiniJson.ParseObjectSafe(text);
                    var choices = dict == null ? null : MiniJson.AsList(MiniJson.Get(dict, "choices"));
                    var first = (choices == null || choices.Count == 0) ? null : MiniJson.AsDict(choices[0]);
                    var message = first == null ? null : MiniJson.AsDict(MiniJson.Get(first, "message"));
                    string content = message == null ? "" : MiniJson.Str(message, "content", "");

                    if (string.IsNullOrEmpty(content))
                    {
                        onError("LLM 返回内容为空。原始响应前 400 字符：\n" + Head(text, 400));
                        return;
                    }

                    onText(content);
                }
                finally
                {
                    try { request.Dispose(); } catch { }
                }
            };

            request.SendWebRequest();
            EditorApplication.update += tick;
        }

        public static void Abort()
        {
            Busy = false;
        }

        // =====================================================================
        // SSE 解析
        // =====================================================================

        class ToolAccumulator
        {
            public string id = "";
            public string name = "";
            public StringBuilder arguments = new StringBuilder();
        }

        class SseHandler : DownloadHandlerScript
        {
            readonly StringBuilder _raw = new StringBuilder();
            readonly StringBuilder _content = new StringBuilder();
            readonly StringBuilder _buffer = new StringBuilder();
            readonly Dictionary<int, ToolAccumulator> _tools = new Dictionary<int, ToolAccumulator>();
            readonly Action<string> _onDelta;
            readonly Encoding _encoding = new UTF8Encoding(false);

            bool _sawSse;
            string _finishReason = "";

            public SseHandler(Action<string> onDelta) : base(new byte[8192])
            {
                _onDelta = onDelta;
            }

            public string RawText { get { return _raw.ToString(); } }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return false;
                string chunk = _encoding.GetString(data, 0, dataLength);
                _raw.Append(chunk);
                _buffer.Append(chunk);
                ProcessBuffer();
                return true;
            }

            protected override string GetText() { return _raw.ToString(); }

            void ProcessBuffer()
            {
                string text = _buffer.ToString();
                int index;
                while ((index = text.IndexOf('\n')) >= 0)
                {
                    string line = text.Substring(0, index).TrimEnd('\r');
                    text = text.Substring(index + 1);
                    HandleLine(line);
                }
                _buffer.Length = 0;
                _buffer.Append(text);
            }

            void HandleLine(string line)
            {
                if (line.Length == 0) return;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) return;

                _sawSse = true;
                string payload = line.Substring(5).Trim();
                if (payload.Length == 0) return;
                if (payload == "[DONE]") return;

                var dict = MiniJson.ParseObjectSafe(payload);
                if (dict != null) Consume(dict);
            }

            void Consume(Dictionary<string, object> dict)
            {
                var choices = MiniJson.AsList(MiniJson.Get(dict, "choices"));
                if (choices == null) return;

                for (int i = 0; i < choices.Count; i++)
                {
                    var choice = MiniJson.AsDict(choices[i]);
                    if (choice == null) continue;

                    var carrier = MiniJson.AsDict(MiniJson.Get(choice, "delta"));
                    if (carrier == null) carrier = MiniJson.AsDict(MiniJson.Get(choice, "message"));
                    if (carrier != null)
                    {
                        string piece = MiniJson.Str(carrier, "content");
                        if (!string.IsNullOrEmpty(piece))
                        {
                            _content.Append(piece);
                            if (_onDelta != null) _onDelta(piece);
                        }
                        AccumulateToolCalls(MiniJson.AsList(MiniJson.Get(carrier, "tool_calls")));
                    }

                    string finish = MiniJson.Str(choice, "finish_reason");
                    if (!string.IsNullOrEmpty(finish)) _finishReason = finish;
                }
            }

            void AccumulateToolCalls(List<object> calls)
            {
                if (calls == null) return;
                for (int i = 0; i < calls.Count; i++)
                {
                    var call = MiniJson.AsDict(calls[i]);
                    if (call == null) continue;

                    int index = MiniJson.Int(call, "index", i);
                    ToolAccumulator acc;
                    if (!_tools.TryGetValue(index, out acc))
                    {
                        acc = new ToolAccumulator();
                        _tools[index] = acc;
                    }

                    string id = MiniJson.Str(call, "id");
                    if (!string.IsNullOrEmpty(id)) acc.id = id;

                    var fn = MiniJson.AsDict(MiniJson.Get(call, "function"));
                    if (fn == null) continue;

                    string name = MiniJson.Str(fn, "name");
                    if (!string.IsNullOrEmpty(name)) acc.name = name;

                    string args = MiniJson.Str(fn, "arguments");
                    if (!string.IsNullOrEmpty(args)) acc.arguments.Append(args);
                }
            }

            /// <summary>把流式片段拼装成一条标准的 assistant 消息。</summary>
            public Dictionary<string, object> BuildMessage()
            {
                // 非流式回退：整个响应体就是一个完整 JSON
                if (!_sawSse)
                {
                    var whole = MiniJson.ParseObjectSafe(_raw.ToString());
                    if (whole != null) Consume(whole);
                }

                if (_content.Length == 0 && _tools.Count == 0)
                {
                    var err = MiniJson.ParseObjectSafe(_raw.ToString());
                    var errorObj = err == null ? null : MiniJson.AsDict(MiniJson.Get(err, "error"));
                    if (errorObj != null)
                        return null;
                }

                var message = new Dictionary<string, object>();
                message["role"] = "assistant";
                message["content"] = _content.Length > 0 ? _content.ToString() : null;

                if (_tools.Count > 0)
                {
                    var keys = new List<int>(_tools.Keys);
                    keys.Sort();

                    var calls = new List<object>(keys.Count);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var acc = _tools[keys[i]];
                        if (string.IsNullOrEmpty(acc.name)) continue;

                        var fn = new Dictionary<string, object>();
                        fn["name"] = acc.name;
                        fn["arguments"] = acc.arguments.Length > 0 ? acc.arguments.ToString() : "{}";

                        var call = new Dictionary<string, object>();
                        call["id"] = string.IsNullOrEmpty(acc.id)
                            ? "call_" + keys[i].ToString(CultureInfo.InvariantCulture)
                            : acc.id;
                        call["type"] = "function";
                        call["function"] = fn;
                        calls.Add(call);
                    }

                    if (calls.Count > 0) message["tool_calls"] = calls;
                }

                return message;
            }
        }
    }
}

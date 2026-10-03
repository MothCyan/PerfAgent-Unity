using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Analysis;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Agent
{
    /// <summary>
    /// Agent 主循环：LLM 决策 → 工具执行 → 结果回灌 → 再决策，直到产出自然语言结论。
    ///
    /// 防幻觉的三道防线在这里落地：
    ///  1. 系统提示词强制「数值只能来自工具返回值」；
    ///  2. 工具结果只含数值与标识，不含解释；
    ///  3. 产出最终回答前跑一遍 EvidenceValidator，把无法回溯的数字标注出来。
    /// </summary>
    public class AgentLoop
    {
        /// <summary>
        /// 单条工具结果进入历史的字符上限。
        ///
        /// 12000 字符 ≈ 3000 token，而每轮请求都要重发整个历史，
        /// 8 轮下来光这一项就吃掉几万 token。4000 字符足够表达一份指标表或一组尖峰帧。
        /// </summary>
        const int MaxToolResultChars = 4000;

        readonly List<object> _messages = new List<object>();
        bool _systemPromptLoaded;
        public bool Busy { get; private set; }
        public int Step { get; private set; }
        public string LastAnswer { get; private set; }

        public event Action<string> OnStatus;

        /// <summary>
        /// 刻意不在构造函数里构造系统提示词。
        /// EditorWindow 的实例字段初始化器运行在 ScriptableObject 构造函数内部，
        /// 那时读取 ScriptableSingleton 会被 Unity 直接拒绝：
        /// “LoadSerializedFileAndForget is not allowed to be called from a ScriptableObject
        /// constructor (or instance field initializer)”，并且会让字段初始化失败。
        /// 所以系统提示词改为首次提问时惰性插入。
        /// </summary>
        public AgentLoop() { }

        /// <summary>清空上下文。系统提示词会在下一次提问时用最新配置重建。</summary>
        public void Reset()
        {
            _messages.Clear();
            _systemPromptLoaded = false;
            Step = 0;
            LastAnswer = "";
        }

        /// <summary>把系统提示词插到最前面。必须晚于 ScriptableObject 构造期才能调用。</summary>
        void EnsureSystemPrompt()
        {
            if (_systemPromptLoaded) return;
            _systemPromptLoaded = true;
            _messages.Insert(0, SystemPrompt());
        }

        public int MessageCount
        {
            get { return _systemPromptLoaded ? _messages.Count : _messages.Count + 1; }
        }

        /// <summary>导出当前上下文，供会话持久化保存。</summary>
        public List<object> ExportMessages()
        {
            // 存盘前也折叠一次：否则关掉面板再打开，历史里那些大块工具结果会原样回来，
            // 内存和后续每一轮的 token 都白省了。折叠是幂等的，重复调用无副作用。
            ToolResultFolder.Fold(_messages);
            return new List<object>(_messages);
        }

        /// <summary>
        /// 恢复历史上下文（域重载 / 重开面板后）。
        /// 历史里的 system 消息会被丢弃，因为预算与快照状态可能已经变了，
        /// 必须等下一次提问时用最新的系统提示词重建。
        /// </summary>
        public void RestoreMessages(List<object> messages)
        {
            _messages.Clear();
            _systemPromptLoaded = false;
            if (messages != null)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    var d = MiniJson.AsDict(messages[i]);
                    if (d == null) continue;
                    if (MiniJson.Str(d, "role", "") == "system") continue;
                    _messages.Add(messages[i]);
                }
            }
            Step = 0;
            LastAnswer = "";
        }

        public void Ask(string userText, Action<string> onDelta, Action<string> onDone, Action<string> onError)
        {
            if (Busy)
            {
                onError("当前已有诊断任务在进行。");
                return;
            }
            if (string.IsNullOrEmpty(userText)) return;

            EnsureSystemPrompt();
            AddMessage("user", userText);
            Busy = true;
            Status("思考中…");
            RunStep(onDelta, onDone, onError);
        }

        void RunStep(Action<string> onDelta, Action<string> onDone, Action<string> onError)
        {
            var cfg = PerfAgentSettings.Config;

            // 每轮发请求前先折叠较早的工具结果。
            // 每轮都要重发整个历史，不折叠的话输入 token 会按平方级涨。
            ToolResultFolder.Fold(_messages);

            if (Step >= cfg.maxSteps)
            {
                // 超出工具调用轮数：强制收尾，避免无限循环与费用失控
                AddMessage("user", "已达到工具调用上限。请立刻基于已有数据给出最终结论，不要再调用工具。");
                Status("收尾中…");
                LlmClient.Send(WithoutTools(_messages), onDelta, delegate (Dictionary<string, object> message)
                {
                    Finish(message, onDone, onError);
                }, delegate (string err)
                {
                    Busy = false;
                    onError(err);
                });
                return;
            }

            LlmClient.Send(_messages, onDelta, delegate (Dictionary<string, object> message)
            {
                _messages.Add(message);

                var calls = MiniJson.AsList(MiniJson.Get(message, "tool_calls"));
                if (calls == null || calls.Count == 0)
                {
                    Finish(message, onDone, onError);
                    return;
                }

                Step++;
                RunTool(calls, 0, onDelta, onDone, onError);
            }, delegate (string err)
            {
                Busy = false;
                onError(err);
            });
        }

        void RunTool(List<object> calls, int index, Action<string> onDelta, Action<string> onDone, Action<string> onError)
        {
            if (index >= calls.Count)
            {
                RunStep(onDelta, onDone, onError);
                return;
            }

            var call = MiniJson.AsDict(calls[index]);
            if (call == null)
            {
                RunTool(calls, index + 1, onDelta, onDone, onError);
                return;
            }

            string callId = MiniJson.Str(call, "id", "call_" + index.ToString(CultureInfo.InvariantCulture));
            var fn = MiniJson.AsDict(MiniJson.Get(call, "function"));
            string name = fn == null ? "" : MiniJson.Str(fn, "name", "");
            string argsText = fn == null ? "{}" : MiniJson.Str(fn, "arguments", "{}");

            var tool = ToolRegistry.Find(name);
            if (tool == null)
            {
                AddToolResult(callId, "{\"error\":\"未知工具: " + name + "\"}");
                RunTool(calls, index + 1, onDelta, onDone, onError);
                return;
            }

            Status("执行工具：" + name);
            var args = MiniJson.ParseObjectSafe(argsText) ?? new Dictionary<string, object>();

            if (tool.invokeAsync != null)
            {
                tool.invokeAsync(args, delegate (object result)
                {
                    AddToolResult(callId, SerializeResult(result));
                    RunTool(calls, index + 1, onDelta, onDone, onError);
                });
                return;
            }

            object sync = null;
            try { sync = tool.invoke(args); }
            catch (Exception e) { sync = ErrorObject("工具执行异常: " + e.Message); }

            AddToolResult(callId, SerializeResult(sync));
            RunTool(calls, index + 1, onDelta, onDone, onError);
        }

        void Finish(Dictionary<string, object> message, Action<string> onDone, Action<string> onError)
        {
            Busy = false;
            string content = MiniJson.Str(message, "content", "");

            if (string.IsNullOrEmpty(content))
            {
                onError("模型没有返回文本结论。");
                return;
            }

            content = Validate(content);
            LastAnswer = content;
            Status("完成");
            onDone(content);
        }

        /// <summary>幻觉校验：把无法回溯的数字显式标注出来，而不是假装它是对的。</summary>
        string Validate(string text)
        {
            var snap = PerfSession.Current;
            if (snap == null) return text;

            List<string> unverified;
            try { unverified = PerfReportExporter.UnverifiedNumbers(text, snap); }
            catch { return text; }

            if (unverified.Count == 0) return text;

            var sb = new StringBuilder(text);
            sb.Append("\n\n---\n**证据校验**：以下数值未能在本次工具返回的证据集中找到出处，请人工复核（可能是模型推断或单位换算产生的偏差）：\n");
            for (int i = 0; i < unverified.Count && i < 12; i++)
                sb.Append("- ").Append(unverified[i]).Append('\n');
            return sb.ToString();
        }

        // =====================================================================
        // 消息构造
        // =====================================================================

        void AddMessage(string role, string content)
        {
            var m = new Dictionary<string, object>();
            m["role"] = role;
            m["content"] = content;
            _messages.Add(m);
        }

        void AddToolResult(string callId, string content)
        {
            if (content != null && content.Length > MaxToolResultChars)
                content = content.Substring(0, MaxToolResultChars) + "\"...(结果过长已截断)\"";

            var m = new Dictionary<string, object>();
            m["role"] = "tool";
            m["tool_call_id"] = callId;
            m["content"] = content ?? "{}";
            _messages.Add(m);
        }

        static List<object> WithoutTools(List<object> messages)
        {
            var copy = new List<object>(messages.Count);
            for (int i = 0; i < messages.Count; i++) copy.Add(messages[i]);
            return copy;
        }

        static string SerializeResult(object result)
        {
            if (result == null) return "{}";
            var s = result as string;
            if (s != null) return s;
            try { return MiniJson.Serialize(result); }
            catch (Exception e) { return "{\"error\":\"序列化失败: " + e.Message + "\"}"; }
        }

        static Dictionary<string, object> ErrorObject(string message)
        {
            var d = new Dictionary<string, object>();
            d["error"] = message;
            return d;
        }

        void Status(string text)
        {
            var handler = OnStatus;
            if (handler != null) handler(text);
        }

        // =====================================================================
        // 系统提示词
        // =====================================================================

        static Dictionary<string, object> SystemPrompt()
        {
            var cfg = PerfAgentSettings.Config;
            var b = cfg.budget;

            var sb = new StringBuilder();
            sb.Append("你是内嵌在 Unity 编辑器中的性能诊断专家 Agent。你的唯一职责是：基于工具实测数据，定位性能问题的根因，并给出可执行的修复方案。\n\n");

            sb.Append("## 铁律（必须遵守）\n");
            sb.Append("1. 任何数值都必须来自工具返回值。绝不允许凭经验估算、推测或编造数字。\n");
            sb.Append("2. 每个结论必须能追溯到具体工具与指标。给出结论时用「指标=数值（工具）」的形式引用证据。\n");
            sb.Append("3. 数据不足时，先调用工具取数，不要猜。工具返回为空要说明「无法取得该数据」而不是绕过它。\n");
            sb.Append("4. 区分「实测事实」与「推断」。推断必须显式标注「推断」，并说明依据。\n");
            sb.Append("5. 预算阈值是判定标准。超标才叫问题，没超标就不要制造问题。\n");
            sb.Append("6. 绝不修改工程文件。任何改动都必须由用户发起：用 get_fix_plan 拿到带 action_id 与风险等级的修复计划，");
            sb.Append("按「收益/成本」排序讲清楚，然后让用户在面板里点击执行。工具里没有、也不会有「直接改工程」的入口。\n\n");

            sb.Append("## 工作方式\n");
            sb.Append("- 先 get_summary 掌握全局，再按瓶颈方向深入：\n");
            sb.Append("  · 帧耗时慢但稳定 → get_markers 找 CPU 热点，get_metrics 看渲染统计\n");
            sb.Append("  · 周期性卡顿尖峰 → get_frames 对比尖峰帧与普通帧，重点看分配量与 GC\n");
            sb.Append("  · 内存高 → get_metrics + get_asset_issues（纹理通常是最大头）\n");
            sb.Append("  · 每帧有分配 → get_code_issues 定位到具体文件与行\n");
            sb.Append("- 如果当前没有快照，先调用 capture_frames 采集（会耗时若干秒，属正常）。\n");
            sb.Append("- 结论要按「收益/成本」排序，优先给出改动小、收益大的项。\n\n");

            sb.Append("## 输出格式\n");
            sb.Append("### 结论\n");
            sb.Append("按严重程度列出，每条包含：现象、证据（含数值）、根因、修复建议、预期收益。\n");
            sb.Append("### 优先级建议\n");
            sb.Append("给出 3 条以内的行动清单，说明先后顺序与理由。\n");
            sb.Append("### 数据局限\n");
            sb.Append("明确说明哪些结论受采样环境（编辑器内 vs 真机）限制。\n\n");

            sb.Append("## 当前性能预算\n");
            sb.Append("目标帧率 ").Append(b.targetFrameRate).Append(" fps，帧预算 ")
              .Append(b.FrameBudgetMs().ToString("0.##", CultureInfo.InvariantCulture)).Append(" ms；")
              .Append("每帧托管分配上限 ").Append(b.maxManagedAllocBytesPerFrame).Append(" B；")
              .Append("Draw Call ").Append(b.maxDrawCalls).Append("；SetPass ").Append(b.maxSetPassCalls).Append("；")
              .Append("三角面 ").Append(b.maxTriangles).Append("；纹理内存 ").Append(b.maxTextureMemoryMB).Append(" MB；")
              .Append("TempAllocator ").Append(b.maxTempAllocatorMB).Append(" MB。\n\n");

            var snap = PerfSession.Current;
            sb.Append("## 当前快照状态\n");
            if (snap == null)
            {
                sb.Append("尚无快照。请先调用 capture_frames，或 load_snapshot 加载历史快照。\n\n");
            }
            else
            {
                sb.Append("id=").Append(snap.id)
                  .Append("，采样 ").Append(snap.frames.Count).Append(" 帧")
                  .Append("，指标 ").Append(snap.metrics.Count).Append(" 条")
                  .Append("，结论 ").Append(snap.findings.Count).Append(" 条")
                  .Append("，资源问题 ").Append(snap.assetIssues.Count).Append(" 条")
                  .Append("，场景问题 ").Append(snap.sceneIssues.Count).Append(" 条")
                  .Append("，代码问题 ").Append(snap.codeIssues.Count).Append(" 条。\n\n");
            }

            if (!cfg.allowSourceCodeUpload)
                sb.Append("注意：用户已关闭源码上传。代码工具返回的代码片段会被隐藏，你无法看到源码内容，请基于模式名与位置给建议。\n");

            var m = new Dictionary<string, object>();
            m["role"] = "system";
            m["content"] = sb.ToString();
            return m;
        }
    }
}

using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 对话历史体检的回归。
    ///
    /// 这批用例钉的是实测事故：历史里留下一条 `content=null` 的 assistant 消息后，
    /// **之后每一次** LLM 请求都会被拒：
    ///   HTTP 400 Invalid assistant message: content or tool_calls must be set
    /// 用户看到的就是「莫名其妙一直报错」，而这跟当前问题毫无关系。
    /// 所以清理逻辑必须逐条钉死，宁可丢历史也不能让对话卡死。
    /// </summary>
    static class MessageHygieneTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(HygieneDropsEmptyAssistantMessage);
            tests.Add(HygieneKeepsValidConversation);
            tests.Add(HygieneDropsOrphanToolResult);
            tests.Add(HygieneDropsHalfFinishedToolRound);
            tests.Add(HygieneToleratesBrokenEntries);
        }

        static Dictionary<string, object> Msg(string role, string content)
        {
            var m = new Dictionary<string, object>();
            m["role"] = role;
            if (content != null) m["content"] = content;
            else m["content"] = null;
            return m;
        }

        static Dictionary<string, object> AssistantWithCalls(params string[] ids)
        {
            var m = new Dictionary<string, object>();
            m["role"] = "assistant";
            m["content"] = null;
            var calls = new List<object>();
            for (int i = 0; i < ids.Length; i++)
            {
                var fn = new Dictionary<string, object>();
                fn["name"] = "get_frames";
                fn["arguments"] = "{}";
                var c = new Dictionary<string, object>();
                c["id"] = ids[i];
                c["type"] = "function";
                c["function"] = fn;
                calls.Add(c);
            }
            m["tool_calls"] = calls;
            return m;
        }

        static Dictionary<string, object> ToolResult(string callId)
        {
            var m = new Dictionary<string, object>();
            m["role"] = "tool";
            m["tool_call_id"] = callId;
            m["content"] = "{\"ok\":true}";
            return m;
        }

        /// <summary>空 assistant（就是 400 的元凶）必须被删掉，对话才能继续。</summary>
        static void HygieneDropsEmptyAssistantMessage()
        {
            var messages = new List<object>
            {
                Msg("system", "你是性能助手"),
                Msg("user", "每帧分配从哪来？"),
                Msg("assistant", null),          // ← 就是它
                Msg("user", "再说一次")
            };

            int removed = MessageHygiene.Clean(messages);

            Equal(1, removed, "empty assistant must be removed");
            Equal(3, messages.Count, "history after cleaning");
            True(!messages.Exists(delegate (object o)
            {
                var d = MiniJson.AsDict(o);
                return d != null && MiniJson.Str(d, "role", "") == "assistant" && string.IsNullOrEmpty(MiniJson.Str(d, "content", ""));
            }), "no empty assistant may survive");
        }

        /// <summary>合法历史一条都不能动（清理必须幂等且无副作用）。</summary>
        static void HygieneKeepsValidConversation()
        {
            var messages = new List<object>
            {
                Msg("system", "系统"),
                Msg("user", "问"),
                Msg("assistant", "答"),
                Msg("user", "再问"),
                AssistantWithCalls("call_1"),
                ToolResult("call_1"),
                Msg("assistant", "基于工具结果的回答")
            };

            int removed = MessageHygiene.Clean(messages);

            Equal(0, removed, "a valid conversation must not be touched");
            Equal(7, messages.Count, "count must stay the same");
        }

        /// <summary>孤儿 tool 结果（前面没有声明它的 assistant）也必须删。</summary>
        static void HygieneDropsOrphanToolResult()
        {
            var messages = new List<object>
            {
                Msg("user", "问"),
                ToolResult("call_9"),            // 前面没有对应的 assistant tool_calls
                Msg("assistant", "答")
            };

            int removed = MessageHygiene.Clean(messages);

            Equal(1, removed, "orphan tool result must be removed");
            Equal(2, messages.Count, "history after cleaning");
        }

        /// <summary>半截工具回合（声明了 2 个调用、只回来 1 个结果）整组丢掉，不留残缺配对。</summary>
        static void HygieneDropsHalfFinishedToolRound()
        {
            var messages = new List<object>
            {
                Msg("user", "问"),
                AssistantWithCalls("call_a", "call_b"),
                ToolResult("call_a"),            // call_b 的结果还没到
                Msg("assistant", "残缺的收尾")     // 依赖被丢掉的回合，也留不住
            };

            int removed = MessageHygiene.Clean(messages);

            // assistant(tool_calls) + 它的部分结果 = 2 条；最后那条 assistant 内容合法，保留
            Equal(2, removed, "half finished tool round must be dropped as a whole, got " + removed);
            Equal(2, messages.Count, "history after cleaning");
        }

        /// <summary>历史是磁盘上恢复来的，里面可能有 null / 非对象 —— 不能让体检自己崩掉。</summary>
        static void HygieneToleratesBrokenEntries()
        {
            var messages = new List<object> { null, "不是对象", Msg("user", "问"), Msg("assistant", "答") };

            int removed = MessageHygiene.Clean(messages);

            Equal(2, removed, "null and non-object entries must be dropped");
            Equal(2, messages.Count, "only the real messages survive");
            Equal(0, MessageHygiene.Clean(messages), "cleaning again must be a no-op");
        }

        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Equal(int expected, int actual, string label)
        {
            if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}

using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 历史消息体检：把会让服务商直接 400 的「坏消息」剔掉。
    ///
    /// <para><b>为什么需要它（实测事故）</b></para>
    /// 历史会被持久化到会话文件，也可能在一轮中途留下半成品。一旦里面存在一条
    /// <c>{role:"assistant", content:null}</c> 且没有 tool_calls，**之后每一次**请求都会失败：
    /// <code>
    /// HTTP 400 Invalid assistant message: content or tool_calls must be set
    /// </code>
    /// 这跟用户当时问的问题毫无关系 —— 用户看到的现象就是「莫名其妙一直报错」，
    /// 而且重开会话也未必好（历史是从文件恢复的）。
    ///
    /// 所以每次发送前都扫一遍：宁可丢掉一条历史，也不要让整个对话卡死。
    /// 同类需要拦下的还有两种：
    ///   · 孤儿 tool 结果（前面没有声明它的 assistant tool_calls）；
    ///   · 半截的工具回合（tool_calls 声明的 id 没有被结果补齐）。
    /// </summary>
    public static class MessageHygiene
    {
        /// <summary>
        /// 就地清理 <paramref name="messages"/>，返回删掉的条数（0 = 历史本身是干净的）。
        /// 只动结构，不改内容：格式合法但内容不对的消息不归它管。
        /// </summary>
        public static int Clean(List<object> messages)
        {
            if (messages == null || messages.Count == 0) return 0;

            var keep = new List<object>(messages.Count);
            var pending = new HashSet<string>();   // 上一条 assistant 声明过、还没被结果消费的 tool_call id
            int removed = 0;

            for (int i = 0; i < messages.Count; i++)
            {
                var m = MiniJson.AsDict(messages[i]);
                if (m == null) { removed++; continue; }   // 连对象都不是，直接丢

                string role = MiniJson.Str(m, "role", "");

                if (role == "assistant")
                {
                    bool hasContent = !string.IsNullOrEmpty(MiniJson.Str(m, "content", ""));
                    var calls = MiniJson.AsList(MiniJson.Get(m, "tool_calls"));
                    bool hasCalls = calls != null && calls.Count > 0;

                    // 空 assistant：必删（就是它导致 400）
                    if (!hasContent && !hasCalls) { removed++; continue; }

                    pending.Clear();

                    if (hasCalls)
                    {
                        var ids = ToolCallIds(calls);

                        // 往后看：紧随其后的 tool 结果必须把声明的 id 全部补齐
                        int matched = 0;
                        for (int k = i + 1; k < messages.Count && matched < ids.Count; k++)
                        {
                            var t = MiniJson.AsDict(messages[k]);
                            if (t == null || MiniJson.Str(t, "role", "") != "tool") break;
                            if (!ids.Contains(MiniJson.Str(t, "tool_call_id", ""))) break;
                            matched++;
                        }

                        if (matched < ids.Count)
                        {
                            // 半截的工具回合：整组丢掉（含已到的部分结果），别让服务商看到残缺的配对
                            removed++;
                            while (i + 1 < messages.Count)
                            {
                                var t = MiniJson.AsDict(messages[i + 1]);
                                if (t == null || MiniJson.Str(t, "role", "") != "tool") break;
                                i++;
                                removed++;
                            }
                            continue;
                        }

                        foreach (var id in ids) pending.Add(id);
                    }

                    keep.Add(messages[i]);
                    continue;
                }

                if (role == "tool")
                {
                    // 只有被上一条 assistant 声明过的结果才放行，其余都是孤儿
                    if (pending.Remove(MiniJson.Str(m, "tool_call_id", ""))) keep.Add(messages[i]);
                    else removed++;
                    continue;
                }

                // system / user / 其他：原样放行
                pending.Clear();
                keep.Add(messages[i]);
            }

            if (removed > 0)
            {
                messages.Clear();
                messages.AddRange(keep);
            }
            return removed;
        }

        static HashSet<string> ToolCallIds(List<object> calls)
        {
            var ids = new HashSet<string>();
            for (int i = 0; i < calls.Count; i++)
            {
                var c = MiniJson.AsDict(calls[i]);
                if (c == null) continue;
                string id = MiniJson.Str(c, "id", "");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }
    }
}

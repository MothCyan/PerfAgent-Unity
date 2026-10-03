using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using PerfAgent.Utils;

namespace PerfAgent.Core
{
    /// <summary>
    /// 一次诊断会话：既能给用户看的富文本记录，也能原样回灌给 LLM 的消息数组。
    /// </summary>
    [Serializable]
    public class PerfConversation
    {
        public string id = "";
        public string savedUtc = "";
        public string transcript = "";
        /// <summary>与 LLM 交互的原始消息（system / user / assistant / tool），用于域重载后恢复上下文。</summary>
        public List<object> messages = new List<object>();

        public string Label()
        {
            return string.IsNullOrEmpty(id) ? "(未命名会话)" : id;
        }
    }

    /// <summary>
    /// 会话持久化：ProjectSettings/PerfAgent/Conversations/&lt;id&gt;.json。
    ///
    /// 用手写 MiniJson 而不是 JsonUtility，因为消息体里含字典与嵌套数组（tool_calls / arguments），
    /// JsonUtility 不支持这两者。放在 ProjectSettings 下与快照一致，避免污染 Assets 与版本库。
    /// </summary>
    public static class PerfConversationStore
    {
        public static string RootDir
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "PerfAgent"), "Conversations");
            }
        }

        public static void EnsureDirs()
        {
            if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
        }

        public static string Save(PerfConversation conversation)
        {
            if (conversation == null) return "";
            EnsureDirs();
            if (string.IsNullOrEmpty(conversation.id))
                conversation.id = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            conversation.savedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var root = new Dictionary<string, object>();
            root["id"] = conversation.id;
            root["savedUtc"] = conversation.savedUtc;
            root["transcript"] = conversation.transcript ?? "";
            root["messages"] = conversation.messages ?? new List<object>();

            string path = Path.Combine(RootDir, conversation.id + ".json");
            File.WriteAllText(path, MiniJson.Serialize(root));
            return path;
        }

        public static PerfConversation Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var root = MiniJson.ParseObjectSafe(File.ReadAllText(path));
                if (root == null) return null;

                var conversation = new PerfConversation();
                conversation.id = MiniJson.Str(root, "id", Path.GetFileNameWithoutExtension(path));
                conversation.savedUtc = MiniJson.Str(root, "savedUtc", "");
                conversation.transcript = MiniJson.Str(root, "transcript", "");
                var messages = MiniJson.AsList(MiniJson.Get(root, "messages"));
                conversation.messages = messages ?? new List<object>();
                return conversation;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 会话读取失败: " + e.Message);
                return null;
            }
        }

        /// <summary>按文件名倒序（= 时间倒序）返回全部会话文件路径。</summary>
        public static List<string> List()
        {
            var result = new List<string>();
            if (!Directory.Exists(RootDir)) return result;
            var files = Directory.GetFiles(RootDir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            result.AddRange(files);
            return result;
        }

        public static PerfConversation LoadLatest()
        {
            var all = List();
            for (int i = 0; i < all.Count; i++)
            {
                var conversation = Load(all[i]);
                if (conversation != null && !string.IsNullOrEmpty(conversation.transcript)) return conversation;
            }
            return null;
        }

        public static bool Delete(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 会话删除失败: " + e.Message);
            }
            return false;
        }
    }
}

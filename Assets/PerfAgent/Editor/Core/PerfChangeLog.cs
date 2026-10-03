using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>一次一键修复的执行记录。undoPayload 由执行器自己解释，日志本身不关心格式。</summary>
    [Serializable]
    public class PerfFixRecord
    {
        public string utc = "";
        public string actionId = "";
        public string title = "";
        public string kind = "";
        public string risk = "";
        public string findingId = "";
        public bool success;
        public int changedCount;
        public string message = "";
        /// <summary>撤销所需的前值快照；为空表示该动作不可撤销。</summary>
        public string undoPayload = "";
        public bool undone;

        public string Label()
        {
            string time = utc;
            int t = time.IndexOf('T');
            if (t > 0 && time.Length >= t + 9) time = time.Substring(t + 1, 8);
            return time + "  " + title + "（" + changedCount + " 项）";
        }
    }

    [Serializable]
    internal class PerfFixLogFile
    {
        public List<PerfFixRecord> records = new List<PerfFixRecord>();
    }

    /// <summary>
    /// 一键修复的审计日志。
    ///
    /// 存在的意义：批量改导入设置是有副作用的动作，必须留下「什么时候、由谁、按哪条结论、
    /// 改了哪些资源、改前是什么值」的完整记录，否则出问题无法回溯。
    /// 与快照一样落在 ProjectSettings/ 下，不进 Assets、不进版本库。
    /// </summary>
    public static class PerfChangeLog
    {
        const int MaxRecords = 300;

        public static string FilePath
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "PerfAgent"), "FixLog.json");
            }
        }

        public static List<PerfFixRecord> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return new List<PerfFixRecord>();
                var file = JsonUtility.FromJson<PerfFixLogFile>(File.ReadAllText(path));
                if (file == null || file.records == null) return new List<PerfFixRecord>();
                return file.records;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 修复日志读取失败: " + e.Message);
                return new List<PerfFixRecord>();
            }
        }

        public static void Append(PerfFixRecord record)
        {
            if (record == null) return;
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (string.IsNullOrEmpty(record.utc))
                    record.utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                var records = Load();
                records.Add(record);
                if (records.Count > MaxRecords)
                    records.RemoveRange(0, records.Count - MaxRecords);

                var file = new PerfFixLogFile();
                file.records = records;
                File.WriteAllText(path, JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 修复日志写入失败: " + e.Message);
            }
        }

        public static void MarkUndone(PerfFixRecord record)
        {
            if (record == null) return;
            try
            {
                var records = Load();
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i] != record && records[i].utc != record.utc) continue;
                    records[i].undone = true;
                    break;
                }
                var file = new PerfFixLogFile();
                file.records = records;
                File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 修复日志更新失败: " + e.Message);
            }
        }

        public static void Clear()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 修复日志清理失败: " + e.Message);
            }
        }

        public static int Count { get { return Load().Count; } }
    }
}

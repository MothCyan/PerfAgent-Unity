using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// 极简 JSONL 追加日志（一行一条记录）。
    ///
    /// <para><b>为什么先用 JSONL 而不是直接上 SQLite</b></para>
    /// 这两类日志的需求是「流程追溯 + 给 AI 当上下文」，而不是「复杂查询」：
    ///   · 追加写、按时间顺序读、偶尔按 id 找一条；
    ///   · 必须能被人直接打开看（`type operations.jsonl` 就能读，也能直接整份喂给模型）；
    ///   · 工程里目前是**零第三方依赖**，引入 SQLite 会带进原生库与平台分支。
    /// 所以默认实现是 JSONL；SQLite 作为可替换后端（见 <see cref="IAnalysisStore"/>），
    /// 接口不变、随时换 —— 换的时候只需要改 <see cref="JsonlAnalysisStore"/> 那一层。
    ///
    /// <para><b>纪律</b></para>
    /// 日志写失败**绝不能**影响主流程（丢日志比丢修复结果好），所以这里全部 try/catch，
    /// 并且只在真正出问题时打一条警告。文件放在 ProjectSettings/PerfAgent/Log 下，不进 Assets、
    /// 不触发资源导入、也不会被包体带走。
    /// </summary>
    public static class JsonlLog
    {
        /// <summary>单个日志文件超过这个大小就轮转一次（避免长期使用把文件撑到几百 MB）。</summary>
        const long MaxBytes = 8L * 1024 * 1024;

        public static string Dir
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "PerfAgent"), "Log");
            }
        }

        public static string FilePath(string name) { return Path.Combine(Dir, name); }

        public static void EnsureDir()
        {
            try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }
            catch (Exception e) { Debug.LogWarning("[PerfAgent] 建日志目录失败: " + e.Message); }
        }

        /// <summary>追加一行。失败只打警告，不抛。</summary>
        public static void Append(string name, string jsonLine)
        {
            if (string.IsNullOrEmpty(jsonLine)) return;

            try
            {
                EnsureDir();
                string path = FilePath(name);
                RotateIfNeeded(path);
                File.AppendAllText(path, jsonLine + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 写日志失败（" + name + "）: " + e.Message);
            }
        }

        /// <summary>读最后 <paramref name="max"/> 行（新的在后）。读不到就返回空表，不抛。</summary>
        public static List<string> ReadLast(string name, int max)
        {
            var result = new List<string>();
            if (max <= 0) return result;

            try
            {
                string path = FilePath(name);
                if (!File.Exists(path)) return result;

                var lines = File.ReadAllLines(path);
                int start = lines.Length - max;
                if (start < 0) start = 0;
                for (int i = start; i < lines.Length; i++)
                    if (!string.IsNullOrEmpty(lines[i])) result.Add(lines[i]);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 读日志失败（" + name + "）: " + e.Message);
            }
            return result;
        }

        /// <summary>旧文件改名成 .1（只保留一代），够用且不会无限膨胀。</summary>
        static void RotateIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length < MaxBytes) return;

                string backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
            catch { }
        }
    }
}

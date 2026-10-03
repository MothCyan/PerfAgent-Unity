using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using PerfAgent.Utils;

namespace PerfAgent.Core
{
    /// <summary>
    /// 快照存储：放在 ProjectSettings/PerfAgent/Snapshots 下。
    /// 刻意不放进 Assets/，避免每次抓帧都触发资源导入与版本库噪声。
    /// </summary>
    public static class PerfSnapshotStore
    {
        public static string RootDir
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "PerfAgent"), "Snapshots");
            }
        }

        public static string RawDir { get { return Path.Combine(RootDir, "Raw"); } }

        public static void EnsureDirs()
        {
            if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
            if (!Directory.Exists(RawDir)) Directory.CreateDirectory(RawDir);
        }

        public static string Save(PerfSnapshot snapshot)
        {
            EnsureDirs();
            if (string.IsNullOrEmpty(snapshot.id))
                snapshot.id = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(snapshot.capturedUtc))
                snapshot.capturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            string path = Path.Combine(RootDir, snapshot.id + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
            return path;
        }

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

        public static PerfSnapshot Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var snap = JsonUtility.FromJson<PerfSnapshot>(json);
                if (snap != null)
                {
                    if (snap.metrics == null) snap.metrics = new List<PerfMetric>();
                    if (snap.frames == null) snap.frames = new List<FrameStat>();
                    if (snap.markers == null) snap.markers = new List<MarkerStat>();
                    if (snap.assetIssues == null) snap.assetIssues = new List<AssetIssue>();
                    if (snap.sceneIssues == null) snap.sceneIssues = new List<SceneIssue>();
                    if (snap.codeIssues == null) snap.codeIssues = new List<CodeIssue>();
                    if (snap.findings == null) snap.findings = new List<PerfFinding>();
                    if (snap.notes == null) snap.notes = new List<string>();
                    if (snap.capturedSources == null) snap.capturedSources = new List<string>();
                }
                return snap;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 快照读取失败: " + e.Message);
                return null;
            }
        }

        public static PerfSnapshot Latest()
        {
            var all = List();
            return all.Count == 0 ? null : Load(all[0]);
        }

        public static bool Delete(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 快照删除失败: " + e.Message);
            }
            return false;
        }

        /// <summary>把 baseline 的指标填进 current.previous / delta，并返回回归项名称列表。</summary>
        public static List<string> Diff(PerfSnapshot baseline, PerfSnapshot current)
        {
            var regressions = new List<string>();
            if (baseline == null || current == null) return regressions;

            for (int i = 0; i < current.metrics.Count; i++)
            {
                var m = current.metrics[i];
                var b = baseline.FindMetric(m.name);
                if (b == null) continue;
                m.previous = b.value;
                m.delta = m.value - b.value;
                if (Math.Abs(m.delta) > 1e-6) regressions.Add(m.name);
            }
            return regressions;
        }

        /// <summary>把当前预算写进指标，便于报告里直接展示「值 / 预算」。</summary>
        public static void ApplyBudget(PerfSnapshot snapshot, PerfBudget budget)
        {
            if (snapshot == null || budget == null) return;
            SetBudget(snapshot, "主线程帧耗时", budget.FrameBudgetMs(), "ms");
            SetBudget(snapshot, "每帧托管分配", budget.maxManagedAllocBytesPerFrame, "B");
            SetBudget(snapshot, "Draw Calls", budget.maxDrawCalls, "次");
            SetBudget(snapshot, "SetPass Calls", budget.maxSetPassCalls, "次");
            SetBudget(snapshot, "Triangles", budget.maxTriangles, "个");
            SetBudget(snapshot, "TempAllocator", budget.maxTempAllocatorMB * 1024L * 1024L, "B");
            SetBudget(snapshot, "总分配内存", budget.maxTotalMemoryMB * 1024L * 1024L, "B");
            SetBudget(snapshot, "纹理内存(估算)", budget.maxTextureMemoryMB * 1024L * 1024L, "B");
        }

        static void SetBudget(PerfSnapshot s, string metricName, double value, string unit)
        {
            var m = s.FindMetric(metricName);
            if (m == null) return;
            m.budget = value.ToString("0.##", CultureInfo.InvariantCulture);
            m.budgetUnit = unit;
            if (value > 0)
                m.severity = Severity.FromRatio(m.value, value * 0.8, value);
        }
    }
}

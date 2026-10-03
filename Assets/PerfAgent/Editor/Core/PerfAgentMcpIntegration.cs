using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// MCP 集成开关：把 PerfAgent 的采集能力与只读结论暴露给外部 MCP 客户端。
    ///
    /// 为什么桥接是独立包：桥接代码必须带 [McpForUnityTool] 特性并引用 MCPForUnity.Editor，
    /// 这是**编译期硬依赖** —— 卸载 MCP 包就会编译报错，所以不能放进主包，也不能默认启用。
    ///
    /// 为什么直接改 manifest.json 文件，而不用 Client.Add / Client.Remove：
    /// 那两个 API 会发起需要**独占 Packages/ 目录**的 UPM 操作，
    /// 与编辑器自身的包解析（例如用户恰好在 Package Manager 里操作）撞车时会报
    ///   Failed to resolve packages: An operation that requires exclusive access to a resource
    ///   (…/Packages/) is already running and must be completed before another can be started.
    /// 结果是 No packages loaded —— 整个工程的包都加载不出来，而 AddRequest 依然返回 Success，
    /// 于是我们的日志会误报「已启用」。改文件则不抢锁：Unity 的文件监听会自己排队解析，
    /// 而且只在 dependencies 开头插/删一行，不改动文件其余内容。
    /// </summary>
    public static class PerfAgentMcpIntegration
    {
        const string BridgePackageName = "com.night.perfagent.mcpbridge";
        const string BridgeFolderName = "PerfAgent.McpForUnity";
        const string McpPackageName = "com.coplaydev.unity-mcp";
        const string MenuRoot = "Tools/PerfAgent/MCP 集成/";

        const string DependenciesMarker = "\"dependencies\": {";
        const string PendingKey = "PerfAgent.McpBridge.PendingVerify";

        // 菜单的 validate 每帧都会调用，所以状态查询要缓存。域重载会让静态字段归零。
        static string _cachedPackageRoot;
        static bool _packageRootResolved;
        static bool? _cachedBridgeInstalled;
        static bool? _cachedMcpInstalled;

        public static bool IsEnabled
        {
            get
            {
                if (!_cachedBridgeInstalled.HasValue)
                    _cachedBridgeInstalled = IsPackageRegistered(BridgePackageName);
                return _cachedBridgeInstalled.Value;
            }
        }

        public static bool IsMcpForUnityInstalled
        {
            get
            {
                if (!_cachedMcpInstalled.HasValue)
                    _cachedMcpInstalled = IsPackageRegistered(McpPackageName);
                return _cachedMcpInstalled.Value;
            }
        }

        static bool IsPackageRegistered(string packageName)
        {
            try
            {
                // 必须写全名：UnityEditor 下同时存在 PackageManager.PackageInfo 与旧的 UnityEditor.PackageInfo
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                for (int i = 0; i < packages.Length; i++)
                    if (packages[i].name == packageName) return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 查询包列表失败: " + e.Message);
            }
            return false;
        }

        static string PackageRoot()
        {
            if (_packageRootResolved) return _cachedPackageRoot;

            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PerfAgentMcpIntegration).Assembly);
                _cachedPackageRoot = info == null ? null : info.resolvedPath;
            }
            catch
            {
                _cachedPackageRoot = null;
            }

            _packageRootResolved = true;
            return _cachedPackageRoot;
        }

        static string BridgeSourcePath()
        {
            string root = PackageRoot();
            if (string.IsNullOrEmpty(root)) return null;

            string parent = Path.GetDirectoryName(root);
            return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, BridgeFolderName);
        }

        static string ManifestPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null) return null;
            return Path.Combine(Path.Combine(projectRoot.FullName, "Packages"), "manifest.json");
        }

        // =====================================================================
        // 菜单
        // =====================================================================

        [MenuItem(MenuRoot + "启用 MCP 集成", false, 200)]
        static void Enable()
        {
            string bridgePath = BridgeSourcePath();
            if (string.IsNullOrEmpty(bridgePath))
            {
                EditorUtility.DisplayDialog("PerfAgent",
                    "MCP 集成只对通过 UPM（Packages/com.night.perfagent）安装的包可用。", "知道了");
                return;
            }

            if (!Directory.Exists(bridgePath))
            {
                EditorUtility.DisplayDialog("PerfAgent",
                    "找不到桥接包：\n" + bridgePath + "\n\n它应该与 PerfAgent 放在同一层目录里。", "知道了");
                return;
            }

            if (!IsMcpForUnityInstalled)
            {
                EditorUtility.DisplayDialog("PerfAgent",
                    "没有检测到 MCP for Unity 包（" + McpPackageName + "）。\n\n" +
                    "桥接包需要引用它的程序集才能编译，请先安装并等 Unity 编译通过，再回来启用。", "知道了");
                return;
            }

            if (IsEnabled)
            {
                EditorUtility.DisplayDialog("PerfAgent", "MCP 集成已经启用了。", "知道了");
                return;
            }

            string manifest = ManifestPath();
            if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest))
            {
                EditorUtility.DisplayDialog("PerfAgent", "找不到 Packages/manifest.json。", "知道了");
                return;
            }

            if (!EditorUtility.DisplayDialog("PerfAgent",
                "将在 Packages/manifest.json 的 dependencies 里加入一行：\n\n" +
                "  \"" + BridgePackageName + "\": \"file:" + RelativeBridgePath(bridgePath) + "\"\n\n" +
                "Unity 会检测到文件变化并自动解析、编译桥接包（不要同时在 Package Manager 里操作）。\n" +
                "完成后重启 MCP 服务器（或重连客户端），即可看到 7 个 perf_* 工具。\n\n" +
                "确认启用？", "启用", "取消")) return;

            if (!WriteManifest(manifest, true, bridgePath)) return;

            SessionState.SetString(PendingKey, BridgePackageName);
            Debug.Log("[PerfAgent] 已写入 manifest.json，Unity 会自动解析桥接包（" + BridgePackageName + "）…");
        }

        [MenuItem(MenuRoot + "禁用 MCP 集成", false, 201)]
        static void Disable()
        {
            if (!IsEnabled) return;

            string manifest = ManifestPath();
            if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest)) return;

            if (!EditorUtility.DisplayDialog("PerfAgent",
                "将从 Packages/manifest.json 移除桥接包依赖（" + BridgePackageName + "）。\n\n" +
                "PerfAgent 本身不受影响，只是外部 MCP 客户端看不到 perf_* 工具。\n" +
                "桥接包源码不会被删除，随时可以再启用。\n\n确认禁用？", "禁用", "取消")) return;

            if (!WriteManifest(manifest, false, null)) return;

            SessionState.EraseString(PendingKey);
            Debug.Log("[PerfAgent] 已从 manifest.json 移除桥接包依赖，Unity 会自动重新解析。");
        }

        /// <summary>只在 dependencies 开头插入/删除一行，其余内容原样保留。</summary>
        static bool WriteManifest(string manifestPath, bool add, string bridgePath)
        {
            try
            {
                string text = File.ReadAllText(manifestPath);
                string result = add ? AddDependency(text, bridgePath) : RemoveDependency(text);

                if (result == null)
                {
                    EditorUtility.DisplayDialog("PerfAgent",
                        "manifest.json 结构不符合预期（找不到 \"dependencies\": { 或已经存在该依赖）。\n" +
                        "请手动编辑。", "知道了");
                    return false;
                }

                File.WriteAllText(manifestPath, result);
                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[PerfAgent] 写入 manifest.json 失败: " + e);
                EditorUtility.DisplayDialog("PerfAgent", "写入 manifest.json 失败：\n" + e.Message, "知道了");
                return false;
            }
        }

        static string AddDependency(string text, string bridgePath)
        {
            if (text.IndexOf(BridgePackageName, StringComparison.Ordinal) >= 0) return null;

            int index = text.IndexOf(DependenciesMarker, StringComparison.Ordinal);
            if (index < 0) return null;

            string line = "\n    \"" + BridgePackageName + "\": \"file:" + RelativeBridgePath(bridgePath) + "\",";
            return text.Insert(index + DependenciesMarker.Length, line);
        }

        /// <summary>
        /// manifest 里的 file: 路径是相对 Packages/ 目录的（所以主包写的是 file:../PerfAgent）。
        /// 优先用相对写法：与主包一致、换机器仍然有效；只有在桥接包不在工程同级目录时才退回绝对路径。
        /// </summary>
        static string RelativeBridgePath(string bridgePath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null) return ToUnityPath(bridgePath);

            string trimmed = bridgePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed);

            if (string.Equals(parent, projectRoot.FullName, StringComparison.OrdinalIgnoreCase))
                return "../" + Path.GetFileName(trimmed);

            return ToUnityPath(bridgePath);
        }

        static string RemoveDependency(string text)
        {
            string prefix = "\"" + BridgePackageName + "\"";
            var kept = new List<string>();
            bool removed = false;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!removed && lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }
                kept.Add(lines[i]);
            }

            if (!removed) return null;

            // 万一被删的是最后一项，前一行会留下尾逗号 —— 去掉它，否则 JSON 非法
            return Regex.Replace(string.Join("\n", kept), ",(\\s*)}", "$1}");
        }

        // =====================================================================
        // 解析结果验证（跨域重载）
        // =====================================================================

        /// <summary>
        /// 写完 manifest 后 Unity 会重新解析包并触发域重载，所以结果要在重载之后检查。
        /// 用 SessionState 记下「待验证」，只报告一次。
        /// </summary>
        [InitializeOnLoadMethod]
        static void CheckPendingVerify()
        {
            string pending = SessionState.GetString(PendingKey, "");
            if (string.IsNullOrEmpty(pending)) return;

            SessionState.EraseString(PendingKey);

            EditorApplication.delayCall += delegate
            {
                InvalidateCache();
                if (IsPackageRegistered(pending))
                {
                    Debug.Log("[PerfAgent] MCP 集成已生效（" + pending + "）。暴露的工具：" +
                              "perf_list_snapshots / perf_capture_start / perf_capture_status / perf_static_audit / " +
                              "perf_get_findings / perf_get_metrics / perf_get_fix_plan。" +
                              "重启 MCP 服务器或重连客户端后即可看到。");
                }
                else
                {
                    // 包解析是异步的，此刻还没就绪不代表失败 —— 不报警告，让用户用「状态」菜单确认
                    Debug.Log("[PerfAgent] 已提交 manifest 改动，但此刻桥接包（" + pending + "）尚未解析完成。" +
                              "若持续如此，请检查 Console 里的包解析错误，或用「Tools > PerfAgent > MCP 集成 > 状态」确认。");
                }
            };
        }

        static void InvalidateCache()
        {
            _cachedBridgeInstalled = null;
            _cachedMcpInstalled = null;
        }

        // =====================================================================
        // 状态
        // =====================================================================

        [MenuItem(MenuRoot + "状态", false, 202)]
        static void ShowStatus()
        {
            InvalidateCache();

            var sb = new StringBuilder();
            sb.Append("MCP for Unity 包：").Append(IsMcpForUnityInstalled ? "已安装" : "未安装").Append('\n');
            sb.Append("MCP 集成：").Append(IsEnabled ? "已启用" : "未启用").Append('\n');

            string bridge = BridgeSourcePath();
            sb.Append("桥接包位置：").Append(string.IsNullOrEmpty(bridge) ? "（未找到）" : bridge).Append("\n\n");

            if (IsEnabled)
            {
                sb.Append("暴露的工具（全部只读或触发分析，没有执行修复的入口）：\n");
                sb.Append("  perf_list_snapshots   列快照\n");
                sb.Append("  perf_static_audit     静态审计（秒级）\n");
                sb.Append("  perf_capture_start    开始抓帧\n");
                sb.Append("  perf_capture_status   查抓帧进度\n");
                sb.Append("  perf_get_findings     取结论 + 证据链\n");
                sb.Append("  perf_get_metrics      取指标 + 数据来源\n");
                sb.Append("  perf_get_fix_plan     取一键修复计划\n\n");
                sb.Append("一键修复的执行入口只存在于 Unity 面板里，外部客户端无法触发修改。");
            }
            else
            {
                sb.Append("用本菜单的「启用 MCP 集成」把 PerfAgent 暴露给外部 MCP 客户端。\n\n");
                sb.Append("默认关闭的原因：桥接包必须引用 MCPForUnity.Editor 程序集，\n");
                sb.Append("启用后卸载 MCP for Unity 会导致桥接包编译报错（PerfAgent 主包不受影响）。");
            }

            EditorUtility.DisplayDialog("PerfAgent MCP 集成", sb.ToString(), "好");
        }

        [MenuItem(MenuRoot + "启用 MCP 集成", true)]
        static bool EnableValidate() { return !IsEnabled; }

        [MenuItem(MenuRoot + "禁用 MCP 集成", true)]
        static bool DisableValidate() { return IsEnabled; }

        static string ToUnityPath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }
    }
}

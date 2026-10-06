using System;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 「这个脚本要不要被反模式扫描跳过」的判定。
    ///
    /// 抽成纯逻辑（不引用 UnityEngine / UnityEditor）是为了能离线回归 —— 这里出过一次**静默失效**事故：
    /// 判定用的曾是**绝对路径**上的 <c>Contains("/perfagent/")</c>，本意是「排除插件自己装的那份」，
    /// 结果工程恰好放在 <c>D:\hub\PerfAgent\</c> 下时，**每一个** .cs 的绝对路径里都含 <c>/perfagent/</c> ——
    /// 整份工程被判成「插件自身」，扫描数永远是 0，用户看到的是「代码问题数 0 个」。
    ///
    /// 静默失效比误报更危险：报告照样「干净」，问题只是没人告诉你。
    ///
    /// 现在的口径：**只认相对工程根的路径**（形如 <c>Assets/xxx/yyy.cs</c>），绝对路径不参与任何判定。
    /// </summary>
    public static class ScriptScope
    {
        /// <param name="assetRelativePath">相对工程根的路径，形如 "Assets/xxx/yyy.cs"（大小写与斜杠都不敏感）</param>
        /// <param name="ownRoot">插件自身的源码根，形如 "Assets/PerfAgent"；空串表示未知（退化为只做通用排除）</param>
        /// <param name="inEditorOnlyAssembly">该文件是否属于「只编给编辑器用」的程序集</param>
        /// <returns>true = 跳过（不参与反模式扫描）</returns>
        public static bool ShouldSkip(string assetRelativePath, string ownRoot, bool inEditorOnlyAssembly)
        {
            string rel = Normalize(assetRelativePath);
            if (rel.Length == 0) return true;   // 拿不到路径就别扫：报出去也没法定位

            // 第三方 / 生成代码 / 缓存目录
            if (rel.Contains("/textmesh pro/")) return true;
            if (rel.Contains("/packages/")) return true;
            if (rel.Contains("/library/")) return true;
            if (rel.Contains("/obj/")) return true;
            if (rel.EndsWith(".meta", StringComparison.Ordinal)) return true;
            if (rel.EndsWith(".designer.cs", StringComparison.Ordinal)) return true;
            if (rel.EndsWith(".g.cs", StringComparison.Ordinal)) return true;
            if (rel.EndsWith(".generated.cs", StringComparison.Ordinal)) return true;

            // Unity 约定：名为 Editor 的文件夹（以及 *.Editor.cs）不参与构建，
            // 里面的 OnGUI / Update 只在编辑器里跑，跟玩家端的每帧开销无关。
            // 「SettingsProvider.OnGUI(string)」这类编辑器 IMGUI 回调被当成每帧方法，正是从这条路径进来的误报。
            if (rel.Contains("/editor/")) return true;
            if (rel.EndsWith(".editor.cs", StringComparison.Ordinal)) return true;

            // asmdef 里 includePlatforms 只有 Editor 的程序集：整个程序集都不参与构建
            if (inEditorOnlyAssembly) return true;

            // 工具自身（它的每帧开销不是项目的问题）
            string own = Normalize(ownRoot);
            if (own.Length > 0 && (rel == own || rel.StartsWith(own + "/", StringComparison.Ordinal)))
            {
                return true;
            }
            // 名字里带 perfagent 的第三方目录（例如插件的 MCP 桥）
            if (rel.Contains("/perfagent/") || rel.Contains("/perfagent.mcp")) return true;

            return false;
        }

        /// <summary>
        /// 归一化成「小写 + 正斜杠」，用来做大小写不敏感的路径比较。
        /// 注意：只做字符串归一化，**不会**把绝对路径变成相对路径（那是调用方的事）。
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Replace('\\', '/').Trim();
            while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal))
            {
                p = p.Substring(0, p.Length - 1);
            }
            return p.ToLowerInvariant();
        }
    }
}

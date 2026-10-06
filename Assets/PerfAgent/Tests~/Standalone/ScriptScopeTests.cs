using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 脚本反模式扫描「跳过谁」的回归。
    ///
    /// 重点是那次**静默失效**：工程放在 `D:\hub\PerfAgent\` 下时，绝对路径里含 `/perfagent/`，
    /// 于是整份工程被判成「插件自身」、扫描数恒为 0 —— 报告照样干净，却一条问题都报不出来。
    /// 所以「该扫的必须扫」比「不该扫的别扫」更重要，这里两条都钉住。
    /// </summary>
    static class ScriptScopeTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(ProjectFilesAreScannedEvenWhenProjectFolderIsNamedPerfAgent);
            tests.Add(PluginOwnSourcesAreSkipped);
            tests.Add(EditorOnlySourcesAreSkipped);
            tests.Add(SampleFixturesAreScanned);
            tests.Add(ThirdPartyNamedPerfAgentIsSkipped);
            tests.Add(GeneratedAndMetaFilesAreSkipped);
            tests.Add(EmptyPathIsSkipped);
        }

        /// <summary>工程目录本身叫 PerfAgent 时，插件以外的脚本必须照常进入扫描。</summary>
        static void ProjectFilesAreScannedEvenWhenProjectFolderIsNamedPerfAgent()
        {
            // 判定只看「相对工程根」的路径；绝对路径（D:/hub/PerfAgent/Assets/...）不参与判定
            False(ScriptScope.ShouldSkip("Assets/PerfAgentSample/Before/Scripts/BeforeTelemetryMonitor.cs",
                "Assets/PerfAgent", false),
                "样例脚本在工程内，必须被扫描（曾因绝对路径含 /perfagent/ 被整份工程跳过）");
            False(ScriptScope.ShouldSkip("Assets/Scripts/GameManager.cs", "Assets/PerfAgent", false),
                "普通业务脚本必须被扫描");
            False(ScriptScope.ShouldSkip("Assets/PerfAgentSample/After/Scripts/AfterGameManager.cs",
                "assets/perfagent/", false),
                "ownRoot 的大小写与结尾斜杠都不该影响判定");
        }

        static void PluginOwnSourcesAreSkipped()
        {
            True(ScriptScope.ShouldSkip("Assets/PerfAgent/Editor/Core/PanelCapture.cs", "Assets/PerfAgent", false),
                "插件自身源码不能报成项目问题");
            True(ScriptScope.ShouldSkip("Assets/PerfAgent/Collectors/Foo.cs", "Assets/PerfAgent", false),
                "插件根目录下的文件同样属于插件自身");
        }

        static void EditorOnlySourcesAreSkipped()
        {
            True(ScriptScope.ShouldSkip("Assets/Tools/Editor/MyTool.cs", "Assets/PerfAgent", false),
                "名为 Editor 的文件夹不参与玩家构建");
            True(ScriptScope.ShouldSkip("Assets/Scripts/MyThing.Editor.cs", "Assets/PerfAgent", false),
                "*.Editor.cs 不参与玩家构建");
            True(ScriptScope.ShouldSkip("Assets/Scripts/Runtime.cs", "Assets/PerfAgent", true),
                "只编给编辑器的程序集整体跳过");
        }

        static void SampleFixturesAreScanned()
        {
            // PerfAgentSample 与插件目录前缀相同，最容易被误杀的就是它
            False(ScriptScope.ShouldSkip("Assets/PerfAgentSample/Before/Scripts/BeforeGameManager.cs",
                "Assets/PerfAgent", false), "Before 样例必须被扫描");
            False(ScriptScope.ShouldSkip("Assets/PerfAgentSample/After/Scripts/AfterTelemetryMonitor.cs",
                "Assets/PerfAgent", false), "After 样例必须被扫描");
            False(ScriptScope.ShouldSkip("Assets/PerfAgentSample/Plugins/Demigiant/DOTween/Modules/DOTweenModuleUI.cs",
                "Assets/PerfAgent", false), "样例自带的第三方插件也不该被误判成插件自身");
        }

        static void ThirdPartyNamedPerfAgentIsSkipped()
        {
            True(ScriptScope.ShouldSkip("Assets/ThirdParty/PerfAgent/SomeScript.cs", "Assets/PerfAgent", false),
                "目录本身就叫 perfagent 的第三方代码要跳过");
            True(ScriptScope.ShouldSkip("Assets/PerfAgent.McpForUnity/Editor/Bridge.cs", "Assets/PerfAgent", false),
                "MCP 桥属于工具自身");
        }

        static void GeneratedAndMetaFilesAreSkipped()
        {
            True(ScriptScope.ShouldSkip("Assets/Scripts/Foo.designer.cs", "Assets/PerfAgent", false), "designer.cs");
            True(ScriptScope.ShouldSkip("Assets/Scripts/Foo.g.cs", "Assets/PerfAgent", false), "*.g.cs");
            True(ScriptScope.ShouldSkip("Assets/Scripts/Foo.generated.cs", "Assets/PerfAgent", false), "generated.cs");
            True(ScriptScope.ShouldSkip("Assets/Scripts/Foo.cs.meta", "Assets/PerfAgent", false), "meta");
            True(ScriptScope.ShouldSkip("Assets/TextMesh Pro/Scripts/Foo.cs", "Assets/PerfAgent", false), "TMP");
        }

        static void EmptyPathIsSkipped()
        {
            True(ScriptScope.ShouldSkip(null, "Assets/PerfAgent", false), "空路径不扫");
            True(ScriptScope.ShouldSkip("", "Assets/PerfAgent", false), "空路径不扫");
        }

        static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void False(bool condition, string message)
        {
            if (condition) throw new Exception(message);
        }
    }
}

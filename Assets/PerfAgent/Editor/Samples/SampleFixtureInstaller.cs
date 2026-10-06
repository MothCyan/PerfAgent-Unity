using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfAgent.Samples
{
    /// <summary>
    /// 把仓库 `Samples/` 下的前后对照样例工程装进**当前工程**，装完直接 Play + 跟随采集。
    ///
    /// 为什么需要这个菜单：
    /// 1) 样例是两份**独立**的 Unity 工程，而 Unity 只导入 `Assets/` 与 `Packages/`，
    ///    所以它们不会出现在当前工程的 Project 窗口里（要看得去 Unity Hub 里单独打开）；
    /// 2) 快照按工程存放（`ProjectSettings/PerfAgent/Snapshots`），跨工程选不到一起，
    ///    「对比」页也就没法直接比 Before / After。
    /// 装进当前工程后这两个问题一起消失：本工程 Play 就能采，两份快照同工程可直接对比。
    ///
    /// 约束：两版的脚本**同名**（GameManager / Bird / SlingShot / TelemetryMonitor…），
    /// 同一个工程里不能共存，所以一次只装一版；装另一版时会先卸载。
    /// 装载位置固定为 `Assets/PerfAgentFixture`（已在根 .gitignore 里忽略），
    /// 卸载 = 删掉这个目录 + 回滚我们往 TagManager 里加的 Tag / Sorting Layer + 从 Build Settings 移除场景。
    ///
    /// 注意：本菜单只在**本仓库**里可用（需要仓库根的 `Samples/` 目录）。
    /// </summary>
    public static class SampleFixtureInstaller
    {
        const string MenuRoot = "Tools/PerfAgent/样例工程/";
        const string InstallFolder = "Assets/PerfAgentFixture";
        const string VariantMarkerName = "INSTALLED_VARIANT.txt";
        const string SettingsMarkerName = "INSTALLED_PROJECT_SETTINGS.txt";
        const string ScenePath = InstallFolder + "/Scenes/game.unity";

        const string VariantBefore = "before";
        const string VariantAfter = "after";

        /// <summary>
        /// 样例用到的 Tag。样例脚本里 `FindGameObjectsWithTag("Bird")` / `CompareTag("Pig")` 都靠它，
        /// **没定义会直接抛 UnityException**，所以安装时必须补进当前工程的 TagManager。
        /// </summary>
        static readonly string[] RequiredTags = { "Bird", "Brick", "Pig" };

        /// <summary>
        /// 样例用到的 Sorting Layer。id 必须与样例场景里存的 `m_SortingLayerID` 一致，
        /// 否则背景/地面/前景会全部落到 Default 层，画面层次就没了。
        /// 数据来自 `Samples/AngryBirds_*/ProjectSettings/TagManager.asset`。
        /// </summary>
        static readonly SortingLayerSpec[] RequiredSortingLayers =
        {
            new SortingLayerSpec("Background", 1050805185),
            new SortingLayerSpec("Trees", 1182191077),
            new SortingLayerSpec("Floor", 3656647777),
            new SortingLayerSpec("Foreground", 151119413),
        };

        struct SortingLayerSpec
        {
            public readonly string name;
            public readonly uint uniqueId;   // 是 uint：样例里的 ID 会超过 int.MaxValue

            public SortingLayerSpec(string name, uint uniqueId)
            {
                this.name = name;
                this.uniqueId = uniqueId;
            }
        }

        [MenuItem(MenuRoot + "安装 Before（优化之前）", false, 110)]
        static void MenuInstallBefore()
        {
            Install(VariantBefore);
        }

        [MenuItem(MenuRoot + "安装 After（优化之后）", false, 111)]
        static void MenuInstallAfter()
        {
            Install(VariantAfter);
        }

        [MenuItem(MenuRoot + "卸载（清理当前工程）", false, 130)]
        static void MenuUninstall()
        {
            RemoveFixture(true);
        }

        [MenuItem(MenuRoot + "卸载（清理当前工程）", true)]
        static bool ValidateUninstall()
        {
            return AssetDatabase.IsValidFolder(InstallFolder);
        }

        [MenuItem(MenuRoot + "打开缺陷清单（PERF-FAULTS.md）", false, 150)]
        static void MenuOpenFaultList()
        {
            OpenRepoFile("PERF-FAULTS.md");
        }

        [MenuItem(MenuRoot + "打开使用说明（README.md）", false, 151)]
        static void MenuOpenReadme()
        {
            OpenRepoFile("README.md");
        }

        // ---------------------------------------------------------------- 安装

        static void Install(string variant)
        {
            string source = SourceAssetsRoot(variant);
            if (!Directory.Exists(source))
            {
                EditorUtility.DisplayDialog("PerfAgent 样例工程",
                    "找不到样例工程：\n" + source +
                    "\n\n这个菜单只在包含 Samples/ 目录的仓库里可用。\n" +
                    "如果你是从 UPM 安装的插件，请到插件仓库里手动打开 Samples/ 下的两个工程。",
                    "知道了");
                return;
            }

            if (AssetDatabase.IsValidFolder(InstallFolder))
            {
                string installed = ReadInstalledVariant();
                bool same = string.Equals(installed, variant, StringComparison.OrdinalIgnoreCase);
                string question = same
                    ? "当前工程里已经装过「" + Describe(variant) + "」，要重新装一遍吗？"
                    : "当前工程里装的是「" + Describe(installed) + "」。\n" +
                      "两版的脚本同名，不能共存，需要先卸载再装「" + Describe(variant) + "」。\n\n继续？";
                if (!EditorUtility.DisplayDialog("PerfAgent 样例工程", question, "继续", "取消"))
                {
                    return;
                }
                if (!RemoveFixture(false))
                {
                    return;
                }
            }

            // 复制前先让用户保存手上改过的场景，避免被动丢改动
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            try
            {
                int files = CopyTree(source, InstallFolderAbs);

                List<string> addedSettings = MergeProjectSettings();
                WriteMarker(VariantMarkerName, variant);
                WriteMarker(SettingsMarkerName, string.Join("\n", addedSettings.ToArray()));

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AddSceneToBuildSettings();
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                Debug.Log("[PerfAgent] 已把样例装进当前工程：" + Describe(variant) +
                    "\n  位置：" + InstallFolder + "（" + files + " 个文件，含样例自带的 DOTween 插件）" +
                    "\n  场景：" + ScenePath + "（已加入 Build Settings 并已打开）" +
                    "\n  项目设置：" + (addedSettings.Count == 0
                        ? "本工程原本就够用（Tag / Sorting Layer 齐全）"
                        : "补了 " + addedSettings.Count + " 项：" + string.Join("、", addedSettings.ToArray()) +
                          "（卸载时会回滚）") +
                    "\n  测试：Play → PerfAgent 面板 → 跟随采集 → 玩 20~30 秒 → 生成快照" +
                    "\n  对比：装 Before 采一份 → 卸载后装 After 再采一份 → 在「对比」页选这两份快照" +
                    "\n  缺陷清单：Samples/PERF-FAULTS.md" +
                    "\n  说明：这是 Built-in 管线的 2D 场景，在当前 URP 工程里外观可能略有差异，性能口径不受影响。");

                EditorUtility.DisplayDialog("PerfAgent 样例工程",
                    "已安装：" + Describe(variant) +
                    "\n\n场景已打开：" + ScenePath + "\n（已加入 Build Settings）" +
                    "\n\n怎么测：\n1) Play\n2) PerfAgent 面板 →「跟随采集」\n" +
                    "3) 玩 20~30 秒 → 生成快照\n" +
                    "4) 再用「安装 After」采一份，两份快照在同一个工程里，\n    可以直接在「对比」页里选。",
                    "开始测");
            }
            catch (Exception e)
            {
                Debug.LogError("[PerfAgent] 安装样例失败：" + e);
                EditorUtility.DisplayDialog("PerfAgent 样例工程",
                    "安装失败：" + e.Message + "\n\n详见 Console。", "知道了");
            }
        }

        // ---------------------------------------------------------------- 卸载

        static bool RemoveFixture(bool showDialog)
        {
            if (!AssetDatabase.IsValidFolder(InstallFolder))
            {
                if (showDialog)
                {
                    EditorUtility.DisplayDialog("PerfAgent 样例工程",
                        "当前工程里没有装过样例（" + InstallFolder + " 不存在）。", "知道了");
                }
                return true;
            }

            string variant = ReadInstalledVariant();

            // 如果正开着样例场景，先切走：否则删完文件，场景里会留一堆丢失的脚本引用
            Scene active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path) &&
                active.path.StartsWith(InstallFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return false;
                }
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }

            List<string> addedSettings = ReadMarkerLines(SettingsMarkerName);
            RevertProjectSettings(addedSettings);
            RemoveSceneFromBuildSettings();
            AssetDatabase.DeleteAsset(InstallFolder);
            AssetDatabase.Refresh();

            Debug.Log("[PerfAgent] 已卸载样例（" + Describe(variant) + "）：删除了 " + InstallFolder +
                      "，从 Build Settings 移除场景" +
                      (addedSettings.Count == 0 ? "。" : "，并回滚了 " + addedSettings.Count + " 项项目设置。"));
            if (showDialog)
            {
                EditorUtility.DisplayDialog("PerfAgent 样例工程",
                    "已卸载：" + Describe(variant) +
                    "\n\n已删除 " + InstallFolder + "、" +
                    "从 Build Settings 移除场景、回滚安装时补的 Tag / Sorting Layer。\n" +
                    "（快照还在 ProjectSettings/PerfAgent/Snapshots 里，不会被删）", "知道了");
            }
            return true;
        }

        // ---------------------------------------------------------------- 文件搬运

        static int CopyTree(string sourceDir, string destDir)
        {
            int count = 0;
            Directory.CreateDirectory(destDir);

            string[] files = Directory.GetFiles(sourceDir);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    // 跳过「孤儿 meta」：对应资源在样例里本来就缺失（git 存不了空目录），
                    // 复制过来只会让 Unity 刷一堆 can't be found 警告
                    string subject = files[i].Substring(0, files[i].Length - ".meta".Length);
                    if (!File.Exists(subject) && !Directory.Exists(subject))
                    {
                        continue;
                    }
                }
                File.Copy(files[i], Path.Combine(destDir, name), true);
                count++;
            }

            string[] dirs = Directory.GetDirectories(sourceDir);
            for (int i = 0; i < dirs.Length; i++)
            {
                count += CopyTree(dirs[i], Path.Combine(destDir, Path.GetFileName(dirs[i])));
            }
            return count;
        }

        static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        static string InstallFolderAbs
        {
            get { return Path.Combine(ProjectRoot, InstallFolder.Replace('/', Path.DirectorySeparatorChar)); }
        }

        static string SourceProjectName(string variant)
        {
            return string.Equals(variant, VariantAfter, StringComparison.OrdinalIgnoreCase)
                ? "AngryBirds_after"
                : "AngryBirds_before";
        }

        static string SourceAssetsRoot(string variant)
        {
            return Path.Combine(Path.Combine(Path.Combine(ProjectRoot, "Samples"), SourceProjectName(variant)), "Assets");
        }

        static void WriteMarker(string fileName, string content)
        {
            Directory.CreateDirectory(InstallFolderAbs);
            File.WriteAllText(Path.Combine(InstallFolderAbs, fileName),
                content + Environment.NewLine, new UTF8Encoding(false));
        }

        static List<string> ReadMarkerLines(string fileName)
        {
            var result = new List<string>();
            try
            {
                string path = Path.Combine(InstallFolderAbs, fileName);
                if (!File.Exists(path))
                {
                    return result;
                }
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length > 0)
                    {
                        result.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就当作「没记录」，卸载时只删目录、不动项目设置
            }
            return result;
        }

        static string ReadInstalledVariant()
        {
            if (!AssetDatabase.IsValidFolder(InstallFolder))
            {
                return "";
            }
            try
            {
                string path = Path.Combine(InstallFolderAbs, VariantMarkerName);
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Trim().ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                // 落到下面的兜底判断
            }
            return ReadInstalledFromScripts();
        }

        /// <summary>标记文件被手工删掉时的兜底：看 After 独有的注释在不在。</summary>
        static string ReadInstalledFromScripts()
        {
            try
            {
                string script = Path.Combine(Path.Combine(InstallFolderAbs, "Scripts"), "TelemetryMonitor.cs");
                if (!File.Exists(script))
                {
                    return "";
                }
                return File.ReadAllText(script).Contains("【优化之后】") ? VariantAfter : VariantBefore;
            }
            catch (Exception)
            {
                return "";
            }
        }

        static string Describe(string variant)
        {
            if (string.Equals(variant, VariantBefore, StringComparison.OrdinalIgnoreCase))
            {
                return "Before（优化之前，故意塞满每帧反模式）";
            }
            if (string.Equals(variant, VariantAfter, StringComparison.OrdinalIgnoreCase))
            {
                return "After（优化之后，同一玩法）";
            }
            return "未知版本";
        }

        // ---------------------------------------------------------------- 项目设置（Tag / Sorting Layer）

        /// <summary>
        /// 把样例需要的 Tag 与 Sorting Layer 补进当前工程，返回补了哪些（形如 `tag:Bird`、`layer:Floor:3656647777`），
        /// 便于卸载时精确回滚。已存在的项不重复添加，也就不会在卸载时被删掉。
        /// </summary>
        static List<string> MergeProjectSettings()
        {
            var added = new List<string>();
            var so = OpenTagManager();
            if (so == null)
            {
                Debug.LogWarning("[PerfAgent] 读不到 ProjectSettings/TagManager.asset，Tag / Sorting Layer 没补。" +
                                 "如果样例跑起来报 `Tag: Bird is not defined`，请手动在 Tags & Layers 里补：Bird / Brick / Pig。");
                return added;
            }

            SerializedProperty tags = so.FindProperty("tags");
            if (tags != null)
            {
                for (int i = 0; i < RequiredTags.Length; i++)
                {
                    if (HasTag(tags, RequiredTags[i]))
                    {
                        continue;
                    }
                    tags.InsertArrayElementAtIndex(tags.arraySize);
                    tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = RequiredTags[i];
                    added.Add("tag:" + RequiredTags[i]);
                }
            }

            SerializedProperty layers = so.FindProperty("m_SortingLayers");
            if (layers != null)
            {
                for (int i = 0; i < RequiredSortingLayers.Length; i++)
                {
                    SortingLayerSpec spec = RequiredSortingLayers[i];
                    if (HasSortingLayer(layers, spec.name))
                    {
                        continue;
                    }
                    layers.InsertArrayElementAtIndex(layers.arraySize);
                    SerializedProperty element = layers.GetArrayElementAtIndex(layers.arraySize - 1);
                    SerializedProperty name = element.FindPropertyRelative("name");
                    SerializedProperty uniqueId = element.FindPropertyRelative("uniqueID");
                    SerializedProperty locked = element.FindPropertyRelative("locked");
                    if (name != null)
                    {
                        name.stringValue = spec.name;
                    }
                    if (uniqueId != null)
                    {
                        // uniqueID 在 TagManager 里是 int32 存储，这里按位写入以保留大于 int.MaxValue 的 ID
                        uniqueId.intValue = unchecked((int)spec.uniqueId);
                    }
                    if (locked != null)
                    {
                        locked.boolValue = false;
                    }
                    added.Add("layer:" + spec.name + ":" + spec.uniqueId);
                }
            }

            if (added.Count > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
            return added;
        }

        static void RevertProjectSettings(List<string> added)
        {
            if (added == null || added.Count == 0)
            {
                return;
            }
            var so = OpenTagManager();
            if (so == null)
            {
                return;
            }
            SerializedProperty tags = so.FindProperty("tags");
            SerializedProperty layers = so.FindProperty("m_SortingLayers");
            bool dirty = false;

            for (int i = 0; i < added.Count; i++)
            {
                string[] parts = added[i].Split(':');
                if (parts.Length >= 2 && parts[0] == "tag" && tags != null)
                {
                    for (int j = 0; j < tags.arraySize; j++)
                    {
                        if (tags.GetArrayElementAtIndex(j).stringValue == parts[1])
                        {
                            tags.DeleteArrayElementAtIndex(j);
                            dirty = true;
                            break;
                        }
                    }
                }
                else if (parts.Length >= 3 && parts[0] == "layer" && layers != null)
                {
                    for (int j = 0; j < layers.arraySize; j++)
                    {
                        SerializedProperty element = layers.GetArrayElementAtIndex(j);
                        SerializedProperty name = element.FindPropertyRelative("name");
                        if (name != null && name.stringValue == parts[1] &&
                            j > 0 /* Default 层永远保留 */)
                        {
                            layers.DeleteArrayElementAtIndex(j);
                            dirty = true;
                            break;
                        }
                    }
                }
            }

            if (dirty)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
        }

        static SerializedObject OpenTagManager()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0 || assets[0] == null)
            {
                return null;
            }
            return new SerializedObject(assets[0]);
        }

        static bool HasTag(SerializedProperty tags, string tag)
        {
            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    return true;
                }
            }
            return false;
        }

        static bool HasSortingLayer(SerializedProperty layers, string layerName)
        {
            for (int i = 0; i < layers.arraySize; i++)
            {
                SerializedProperty name = layers.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (name != null && name.stringValue == layerName)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- Build Settings

        static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (string.Equals(scenes[i].path, ScenePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!scenes[i].enabled)
                    {
                        scenes[i] = new EditorBuildSettingsScene(ScenePath, true);
                        EditorBuildSettings.scenes = scenes.ToArray();
                    }
                    return;
                }
            }
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void RemoveSceneFromBuildSettings()
        {
            var kept = new List<EditorBuildSettingsScene>();
            EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
            bool changed = false;
            for (int i = 0; i < current.Length; i++)
            {
                if (string.Equals(current[i].path, ScenePath, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }
                kept.Add(current[i]);
            }
            if (changed)
            {
                EditorBuildSettings.scenes = kept.ToArray();
            }
        }

        // ---------------------------------------------------------------- 文档

        static void OpenRepoFile(string fileName)
        {
            string path = Path.Combine(Path.Combine(ProjectRoot, "Samples"), fileName);
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("PerfAgent 样例工程",
                    "找不到文档：\n" + path, "知道了");
                return;
            }
            EditorUtility.OpenWithDefaultApp(path);
        }
    }
}

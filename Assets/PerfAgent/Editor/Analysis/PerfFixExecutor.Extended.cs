using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PerfAgent.Core;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// PerfFixExecutor 的扩展部分（第二批动作）。
    ///
    /// 拆出来的原因：主文件已经承载了「资源导入 + 工程设置」两类动作的骨架，
    /// 而这一批引入了另一种全新形态 —— 新建资产（图集），
    /// 混在一起会让风险等级完全不同的代码互相遮蔽。
    ///
    /// 沿用主文件的纪律：找不到目标就跳过、已经符合预期就跳过、
    /// 改前值一律记进 undoPayload、异常一律收敛成结果消息。
    /// </summary>
    public static partial class PerfFixExecutor
    {
        // =====================================================================
        // 注册与分发
        // =====================================================================

        static bool CanExecuteExtended(string actionId)
        {
            switch (actionId)
            {
                case FixActionIds.SceneLightHardShadow:
                case FixActionIds.SceneCameraDepthOnly:
                case FixActionIds.SceneParticleMaxReduce:
                case FixActionIds.SceneParticleLocal:
                case FixActionIds.SceneMeshColliderConvex:
                case FixActionIds.PhysicsSimulationFixed:
                case FixActionIds.AtlasCreateSprite:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>返回 null 表示这不是扩展动作，由主 switch 报「未注册」。</summary>
        static PerfFixOutcome ExecuteExtended(PerfFixStep step, List<UndoItem> undo)
        {
            switch (step.actionId)
            {
                case FixActionIds.SceneLightHardShadow:
                    return ApplyComponents<Light>(step, undo, MutateHardShadow);
                case FixActionIds.SceneCameraDepthOnly:
                    return ApplyComponents<Camera>(step, undo, MutateDepthOnly);
                case FixActionIds.SceneParticleMaxReduce:
                    return ApplyComponents<ParticleSystem>(step, undo, MutateParticleMax);
                case FixActionIds.SceneParticleLocal:
                    return ApplyComponents<ParticleSystem>(step, undo, MutateParticleLocal);
                case FixActionIds.SceneMeshColliderConvex:
                    return ApplyComponents<MeshCollider>(step, undo, MutateColliderConvex);

                case FixActionIds.PhysicsSimulationFixed:
                    return ApplyProjectSetting(step, undo, delegate (Dictionary<string, string> before)
                    {
                        if (Physics.simulationMode == SimulationMode.FixedUpdate) return false;
                        before["simulationMode"] = Physics.simulationMode.ToString();
                        Physics.simulationMode = SimulationMode.FixedUpdate;
                        AssetDatabase.SaveAssets();
                        return true;
                    });

                case FixActionIds.AtlasCreateSprite:
                    return CreateSpriteAtlas(step, undo);

                default:
                    return null;
            }
        }

        static bool RevertExtended(PerfFixRecord record, List<UndoItem> items, PerfFixOutcome outcome)
        {
            switch (record.actionId)
            {
                case FixActionIds.SceneLightHardShadow:
                    UndoComponents<Light>(items, outcome, "shadows", delegate (Light c, string v)
                    { c.shadows = ParseEnum(v, LightShadows.Hard); });
                    return true;

                case FixActionIds.SceneCameraDepthOnly:
                    UndoComponents<Camera>(items, outcome, "clearFlags", delegate (Camera c, string v)
                    { c.clearFlags = ParseEnum(v, CameraClearFlags.Depth); });
                    return true;

                case FixActionIds.SceneParticleMaxReduce:
                case FixActionIds.SceneParticleLocal:
                    UndoParticles(items, outcome, record.actionId);
                    return true;

                case FixActionIds.SceneMeshColliderConvex:
                    UndoComponents<MeshCollider>(items, outcome, "convex", delegate (MeshCollider c, string v)
                    { c.convex = v == "1"; });
                    return true;

                case FixActionIds.PhysicsSimulationFixed:
                    UndoSimulationMode(items, outcome);
                    return true;

                case FixActionIds.AtlasCreateSprite:
                    UndoCreatedAssets(items, outcome);
                    return true;

                default:
                    return false;
            }
        }

        // =====================================================================
        // 场景：组件级批量修改骨架
        // =====================================================================

        delegate bool ComponentMutator<T>(T component, Dictionary<string, string> before) where T : Component;

        /// <summary>
        /// 按 hierarchyPath 找到对象，改它身上**自己**的 T 组件。
        /// 刻意不用 GetComponentsInChildren —— 审计报的是具体对象，改到子物体属于越界。
        ///
        /// 特例：targets 为空表示这是**场景级结论**（审计只报了数量、没报具体对象，
        /// 比如「3 个非凸 MeshCollider」）。这种情况扫描整个场景找符合条件的目标 ——
        /// 否则这类结论永远只能人工处理。
        /// </summary>
        static PerfFixOutcome ApplyComponents<T>(PerfFixStep step, List<UndoItem> undo, ComponentMutator<T> mutate)
            where T : Component
        {
            var outcome = new PerfFixOutcome();
            var targets = ResolveTargets(step);

            if (targets.Count == 0)
            {
                outcome.message = "没有可操作的目标。";
                return outcome;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                var go = targets[i];
                if (Progress != null) Progress(i, targets.Count, go == null ? "(未找到)" : go.name);

                if (go == null)
                {
                    outcome.skippedCount++;
                    if (outcome.details.Count < 10) outcome.details.Add("未找到目标对象");
                    continue;
                }

                var components = go.GetComponents<T>();
                // 全场景扫描时绝大多数对象都没有该组件，这不算「跳过」，静默略过
                if (components.Length == 0) continue;

                string label = PathOf(go);

                for (int k = 0; k < components.Length; k++)
                {
                    var before = new Dictionary<string, string>();
                    bool changed;
                    try { changed = mutate(components[k], before); }
                    catch (Exception e)
                    {
                        outcome.skippedCount++;
                        if (outcome.details.Count < 10) outcome.details.Add("失败 " + label + "：" + e.Message);
                        continue;
                    }

                    if (!changed) { outcome.skippedCount++; continue; }

                    outcome.changedCount++;
                    if (before.Count > 0) undo.Add(new UndoItem(label, before));
                    if (outcome.details.Count < 10) outcome.details.Add(label);
                }
            }

            outcome.success = outcome.changedCount > 0;
            outcome.needsReanalyze = outcome.changedCount > 0;
            outcome.message = outcome.changedCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "已修改 {0} 处（跳过 {1} 处）", outcome.changedCount, outcome.skippedCount)
                : "没有需要修改的对象（可能已符合预期，或类型不匹配）。";
            return outcome;
        }

        /// <summary>把 step.targets 解析成 GameObject；targets 为空则全场景扫描。</summary>
        static List<GameObject> ResolveTargets(PerfFixStep step)
        {
            var result = new List<GameObject>();

            if (step.targets != null && step.targets.Count > 0)
            {
                for (int i = 0; i < step.targets.Count; i++)
                    result.Add(FindByPath(step.targets[i]));
                return result;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return result;

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null) continue;
                var all = roots[i].GetComponentsInChildren<Transform>(true);
                for (int k = 0; k < all.Length; k++)
                    if (all[k] != null) result.Add(all[k].gameObject);
            }
            return result;
        }

        /// <summary>与 FindByPath 对应的层级路径（用于写进撤销载荷）。</summary>
        static string PathOf(GameObject go)
        {
            if (go == null) return "";

            var sb = new StringBuilder(go.name);
            var parent = go.transform.parent;
            while (parent != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, parent.name);
                parent = parent.parent;
            }
            return sb.ToString();
        }

        // ---- 各动作的改法 ----

        static bool MutateHardShadow(Light light, Dictionary<string, string> before)
        {
            if (light == null) return false;
            // 方向光通常是主光源，软阴影的观感差异最明显，不自动动它
            if (light.type == LightType.Directional) return false;
            // 没有阴影就没什么可省的
            if (light.shadows == LightShadows.None) return false;
            if (light.shadows == LightShadows.Hard) return false;

            before["shadows"] = light.shadows.ToString();
            Undo.RecordObject(light, "PerfAgent: 点光/聚光改为硬阴影");
            light.shadows = LightShadows.Hard;
            EditorUtility.SetDirty(light);
            return true;
        }

        static bool MutateDepthOnly(Camera camera, Dictionary<string, string> before)
        {
            if (camera == null) return false;
            // 主相机负责清屏与天空盒，不能改
            if (camera.CompareTag("MainCamera")) return false;
            if (camera.clearFlags == CameraClearFlags.Depth || camera.clearFlags == CameraClearFlags.Nothing) return false;

            before["clearFlags"] = camera.clearFlags.ToString();
            Undo.RecordObject(camera, "PerfAgent: 副相机改为 Depth Only");
            camera.clearFlags = CameraClearFlags.Depth;
            EditorUtility.SetDirty(camera);
            return true;
        }

        /// <summary>粒子上限降到 500：超过这个量级在移动端几乎必然是 Overdraw 瓶颈。</summary>
        const int ParticleMaxTarget = 500;

        static bool MutateParticleMax(ParticleSystem ps, Dictionary<string, string> before)
        {
            if (ps == null) return false;

            var main = ps.main;
            if (main.maxParticles <= ParticleMaxTarget) return false;

            before["maxParticles"] = main.maxParticles.ToString(CultureInfo.InvariantCulture);
            Undo.RecordObject(ps, "PerfAgent: 降低粒子上限");
            main.maxParticles = ParticleMaxTarget;
            EditorUtility.SetDirty(ps);
            return true;
        }

        static bool MutateParticleLocal(ParticleSystem ps, Dictionary<string, string> before)
        {
            if (ps == null) return false;

            var main = ps.main;
            if (main.simulationSpace == ParticleSystemSimulationSpace.Local) return false;

            before["simulationSpace"] = main.simulationSpace.ToString();
            Undo.RecordObject(ps, "PerfAgent: 粒子改为 Local 模拟空间");
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            EditorUtility.SetDirty(ps);
            return true;
        }

        /// <summary>只对挂着 Rigidbody 的动态物体做凸包化；静态物体的非凸碰撞体是合理用法。</summary>
        static bool MutateColliderConvex(MeshCollider collider, Dictionary<string, string> before)
        {
            if (collider == null || collider.convex) return false;
            if (collider.attachedRigidbody == null) return false;

            before["convex"] = collider.convex ? "1" : "0";
            Undo.RecordObject(collider, "PerfAgent: 动态网格碰撞体改为凸包");
            collider.convex = true;
            EditorUtility.SetDirty(collider);
            return true;
        }

        // =====================================================================
        // 图集：把工程里的 Sprite 贴图收进一个 SpriteAtlas
        // =====================================================================

        /// <summary>
        /// 生成 SpriteAtlas 并收录工程内所有 textureType == Sprite 的贴图。
        ///
        /// 边界说明：Draw Call 偏高的根因要跑 Frame Debugger 才能确定，静态分析猜不出来。
        /// 所以这里只做**确定有效的那一半** —— 把散图合并成图集，让同图集的
        /// UI / 2D 元素具备合批条件。至于 3D 物件的合批，靠「标记 Static」与共享材质，
        /// 不在本动作的职责内。
        /// </summary>
        static PerfFixOutcome CreateSpriteAtlas(PerfFixStep step, List<UndoItem> undo)
        {
            var outcome = new PerfFixOutcome();

            try
            {
                string dir = (step.targets != null && step.targets.Count > 0) ? step.targets[0] : "Assets";
                if (string.IsNullOrEmpty(dir)) dir = "Assets";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    (dir.TrimEnd('/') + "/PerfAgentAtlas.spriteatlas"));

                var atlas = new SpriteAtlas();
                AssetDatabase.CreateAsset(atlas, assetPath);

                var packables = new List<UnityEngine.Object>();
                var guids = AssetDatabase.FindAssets("t:Texture2D");
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (string.IsNullOrEmpty(path)) continue;

                    var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (ti == null || ti.textureType != TextureImporterType.Sprite) continue;

                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (texture != null) packables.Add(texture);
                }

                if (packables.Count > 0)
                {
                    atlas.Add(packables.ToArray());
                    EditorUtility.SetDirty(atlas);
                    AssetDatabase.SaveAssets();
                }

                undo.Add(new UndoItem(assetPath, new Dictionary<string, string> { { "created", "1" } }));

                outcome.changedCount = 1;
                outcome.success = true;
                outcome.needsReanalyze = true;
                outcome.message = packables.Count > 0
                    ? ("已生成图集 " + assetPath + "，收录 " + packables.Count + " 张 Sprite 贴图。")
                    : ("已生成空图集 " + assetPath + "：工程里暂时没有 textureType 为 Sprite 的贴图，"
                       + "手动把需要的图拖进去即可。");
                if (outcome.details.Count < 10) outcome.details.Add(assetPath);
            }
            catch (Exception e)
            {
                outcome.success = false;
                outcome.message = "生成图集失败：" + e.Message;
            }

            return outcome;
        }

        // =====================================================================
        // 撤销
        // =====================================================================

        static void UndoComponents<T>(List<UndoItem> items, PerfFixOutcome outcome, string key, Action<T, string> restore)
            where T : Component
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                string value;
                if (!item.values.TryGetValue(key, out value)) continue;

                var go = FindByPath(item.target);
                if (go == null) continue;

                var components = go.GetComponents<T>();
                for (int k = 0; k < components.Length; k++)
                {
                    Undo.RecordObject(components[k], "PerfAgent: 撤销");
                    restore(components[k], value);
                    EditorUtility.SetDirty(components[k]);
                    outcome.changedCount++;
                }
            }
        }

        static void UndoParticles(List<UndoItem> items, PerfFixOutcome outcome, string actionId)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                var go = FindByPath(item.target);
                if (go == null) continue;

                var systems = go.GetComponents<ParticleSystem>();
                for (int k = 0; k < systems.Length; k++)
                {
                    string value;

                    if (actionId == FixActionIds.SceneParticleMaxReduce && item.values.TryGetValue("maxParticles", out value))
                    {
                        int parsed;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) continue;
                        Undo.RecordObject(systems[k], "PerfAgent: 撤销粒子上限");
                        var main = systems[k].main;
                        main.maxParticles = parsed;
                    }
                    else if (actionId == FixActionIds.SceneParticleLocal && item.values.TryGetValue("simulationSpace", out value))
                    {
                        Undo.RecordObject(systems[k], "PerfAgent: 撤销粒子模拟空间");
                        var main = systems[k].main;
                        main.simulationSpace = ParseEnum(value, ParticleSystemSimulationSpace.World);
                    }
                    else continue;

                    EditorUtility.SetDirty(systems[k]);
                    outcome.changedCount++;
                }
            }
        }

        static void UndoSimulationMode(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                string value;
                if (!items[i].values.TryGetValue("simulationMode", out value)) continue;

                Physics.simulationMode = ParseEnum(value, SimulationMode.FixedUpdate);
                outcome.changedCount++;
            }

            if (outcome.changedCount > 0) AssetDatabase.SaveAssets();
        }

        static void UndoCreatedAssets(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string flag;
                if (!item.values.TryGetValue("created", out flag)) continue;

                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.target) == null) continue;
                if (AssetDatabase.DeleteAsset(item.target)) outcome.changedCount++;
            }
        }
    }
}

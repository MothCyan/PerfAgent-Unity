using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Collectors
{
    /// <summary>场景结构与物理配置审计。全部基于「组件计数 + 已知反模式」，不依赖 Profiler。</summary>
    public class SceneAuditCollector : IPerfCollector
    {
        public string Name { get { return "场景审计"; } }
        public string ToolName { get { return "audit_scene"; } }
        public string Description { get { return "统计场景内各类组件数量，并检出常见的渲染/动画/粒子/UI 反模式。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            var meshRenderers = SceneObjects<MeshRenderer>();
            var skinned = SceneObjects<SkinnedMeshRenderer>();
            var particles = SceneObjects<ParticleSystem>();
            var lights = SceneObjects<Light>();
            var cameras = SceneObjects<Camera>();
            var canvases = SceneObjects<Canvas>();
            var animators = SceneObjects<Animator>();
            var audioSources = SceneObjects<AudioSource>();
            var colliders = SceneObjects<Collider>();
            var rigidbodies = SceneObjects<Rigidbody>();
            // 2D 游戏里 Collider2D / Rigidbody2D 与 3D 那套是两个独立类型，
            // 只统计 3D 会让一份地道的 2D 场景报告出「Collider 0 / Rigidbody 0」（实测样例就是这么被误读的）
            var colliders2D = SceneObjects<Collider2D>();
            var rigidbodies2D = SceneObjects<Rigidbody2D>();
            var monoBehaviours = SceneObjects<MonoBehaviour>();

            s.SetMetric("MeshRenderer", "个", meshRenderers.Count, "场景内");
            s.SetMetric("SkinnedMeshRenderer", "个", skinned.Count, "场景内");
            s.SetMetric("ParticleSystem", "个", particles.Count, "场景内");
            s.SetMetric("Light", "个", lights.Count, "场景内");
            s.SetMetric("Camera", "个", cameras.Count, "场景内");
            s.SetMetric("Canvas", "个", canvases.Count, "场景内");
            s.SetMetric("Animator", "个", animators.Count, "场景内");
            s.SetMetric("AudioSource", "个", audioSources.Count, "场景内");
            s.SetMetric("Collider", "个", colliders.Count, "场景内（3D）");
            s.SetMetric("Rigidbody", "个", rigidbodies.Count, "场景内（3D）");
            s.SetMetric("Collider2D", "个", colliders2D.Count, "场景内（2D）");
            s.SetMetric("Rigidbody2D", "个", rigidbodies2D.Count, "场景内（2D）");
            s.SetMetric("MonoBehaviour", "个", monoBehaviours.Count, "场景内（含第三方组件）");

            var tmpType = Reflect.FindType("TMPro.TMP_Text");
            int tmpCount = tmpType == null ? 0 : SceneObjects(tmpType).Count;
            if (tmpCount > 0) s.SetMetric("TMP 文本组件", "个", tmpCount, "TMPro.TMP_Text");

            // ---- 反模式 ----

            for (int i = 0; i < skinned.Count && i < ctx.topN; i++)
            {
                var r = skinned[i];
                if (r.updateWhenOffscreen)
                    Add(s, r, "SkinnedMeshRenderer", "updateWhenOffscreen = true，即使不在视野内也会持续蒙皮计算",
                        "关闭 updateWhenOffscreen，并确保模型包围盒正确", Severity.Warn, "scene_skinned_offscreen");
            }

            for (int i = 0; i < particles.Count && i < ctx.topN; i++)
            {
                var ps = particles[i];
                if (ps.main.maxParticles > 2000)
                    Add(s, ps, "ParticleSystem", string.Format(CultureInfo.InvariantCulture, "maxParticles = {0}，粒子上限过高", ps.main.maxParticles),
                        "降低 maxParticles，并检查 Overdraw（大量半透明粒子是移动端常见瓶颈）", Severity.Warn, "scene_particle_max");
                if (ps.emission.rateOverDistanceMultiplier > 0 && ps.main.simulationSpace == ParticleSystemSimulationSpace.World)
                    Add(s, ps, "ParticleSystem", "使用 World 模拟空间 + 距离发射，粒子数量不可控",
                        "改用 Local 空间或限制 maxParticles", Severity.Info, "scene_particle_world_space");
            }

            int realtimeShadowLights = 0;
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                if (l.type == LightType.Directional && l.shadows != LightShadows.None && l.lightmapBakeType != LightmapBakeType.Baked)
                    realtimeShadowLights++;
            }
            if (realtimeShadowLights > ctx.budget.maxRealtimeShadowLights)
                s.SetMetric("实时阴影方向光", "个", realtimeShadowLights, "场景内");

            for (int i = 0; i < lights.Count && i < ctx.topN; i++)
            {
                var l = lights[i];
                if (l.shadows == LightShadows.Soft && l.type != LightType.Directional && l.lightmapBakeType != LightmapBakeType.Baked)
                    Add(s, l, "Light", "点光/聚光使用软阴影，阴影贴图开销高",
                        "改为硬阴影或烘焙（Baked）", Severity.Warn, "scene_light_soft_shadow");
            }

            if (cameras.Count > 1)
            {
                s.SetMetric("相机数量", "个", cameras.Count, "场景内");
                for (int i = 0; i < cameras.Count && i < ctx.topN; i++)
                {
                    var c = cameras[i];
                    if (c.clearFlags == CameraClearFlags.Skybox)
                        Add(s, c, "Camera", "多相机中该相机使用 Skybox 清除，每帧重复绘制天空盒",
                        "改为 Depth Only / Solid Color", Severity.Info, "scene_camera_skybox");
                }
            }

            // 静态批处理漏网：非静态且未参与批处理的渲染器
            int nonStatic = 0;
            for (int i = 0; i < meshRenderers.Count; i++)
            {
                var r = meshRenderers[i];
                if (!r.gameObject.isStatic && !r.isPartOfStaticBatch && r.enabled) nonStatic++;
            }
            if (nonStatic > 0)
                s.SetMetric("未静态且未批处理的渲染器", "个", nonStatic, "场景内");
            if (meshRenderers.Count > 0 && nonStatic > meshRenderers.Count * 0.8f && nonStatic > 50)
                Add(s, null, "MeshRenderer", string.Format(CultureInfo.InvariantCulture, "{0}/{1} 个渲染器未参与任何静态批处理", nonStatic, meshRenderers.Count),
                    "对不移动的物体勾选 Static（Contribute GI / Batching Static）", Severity.Info, "scene_non_static_renderers");

            if (audioSources.Count > ctx.budget.maxAudioSourceCount)
                Add(s, null, "AudioSource", string.Format(CultureInfo.InvariantCulture, "场景内 AudioSource 数量 {0}，超过预算 {1}", audioSources.Count, ctx.budget.maxAudioSourceCount),
                    "复用少量 AudioSource，或改用音频池", Severity.Warn, "scene_audio_source_count");

            if (canvases.Count > 3)
                Add(s, null, "Canvas", string.Format(CultureInfo.InvariantCulture, "场景内 {0} 个 Canvas，每个 Canvas 都会产生独立的合批与重建", canvases.Count),
                    "合并 Canvas，或对高频变化区域拆分独立 Canvas 以限制重建范围", Severity.Info, "scene_canvas_count");

            AuditPhysics(s, ctx, colliders, rigidbodies);
        }

        static void AuditPhysics(PerfSnapshot s, CollectorContext ctx, List<Collider> colliders, List<Rigidbody> rigidbodies)
        {
            var meshColliders = new List<MeshCollider>();
            var nonConvex = 0;
            for (int i = 0; i < colliders.Count; i++)
            {
                var mc = colliders[i] as MeshCollider;
                if (mc != null)
                {
                    meshColliders.Add(mc);
                    if (!mc.convex) nonConvex++;
                }
            }
            s.SetMetric("MeshCollider", "个", meshColliders.Count, "场景内");

            if (ctx.snapshot != null && ctx.snapshot.physicsAutoSyncTransforms)
                Add(s, null, "Physics", "Physics.autoSyncTransforms = true，每次 Transform 变更都会同步物理，开销显著",
                    "关闭 Auto Sync Transforms（Unity 2018.3+ 默认关闭），改为手动 Physics.SyncTransforms()", Severity.Error, "scene_autosync_transforms");

            if (ctx.snapshot != null && ctx.snapshot.physicsSimulationMode != "FixedUpdate"
                && !string.IsNullOrEmpty(ctx.snapshot.physicsSimulationMode))
                Add(s, null, "Physics", "Physics.simulationMode = " + ctx.snapshot.physicsSimulationMode + "，物理不在固定步长更新",
                    "除非需要手动控制，否则保持 FixedUpdate 模式", Severity.Info, "scene_physics_simulation_mode");

            double fixedStep = ctx.snapshot != null ? ctx.snapshot.fixedDeltaTime : Time.fixedDeltaTime;
            if (fixedStep > 0 && fixedStep < 0.01)
                Add(s, null, "Physics", string.Format(CultureInfo.InvariantCulture, "Time.fixedDeltaTime = {0}，物理步进过密", fixedStep),
                    "保持在 0.02（50Hz）；提高精度应优先用连续碰撞检测", Severity.Warn, "scene_fixed_delta_too_small");

            if (nonConvex > 0)
                Add(s, null, "MeshCollider", string.Format(CultureInfo.InvariantCulture, "{0} 个非凸 MeshCollider（仅可用于静态物体，且构建开销高）", nonConvex),
                    "静态场景可用非凸；动态物体请改用凸包或复合基础碰撞体", Severity.Info, "scene_mesh_collider_non_convex");

            if (rigidbodies.Count > 0 && colliders.Count > rigidbodies.Count * 8 && rigidbodies.Count > 20)
                Add(s, null, "Physics", string.Format(CultureInfo.InvariantCulture, "刚体 {0} 个但碰撞体 {1} 个，复合碰撞体过多会拉高宽相检测成本", rigidbodies.Count, colliders.Count),
                    "精简子碰撞体，或使用单凸包近似", Severity.Info, "scene_collider_rigidbody_ratio");

            s.SetMetric("Rigidbody", "个", rigidbodies.Count, "场景内");
        }

        static void Add(PerfSnapshot s, Component c, string componentType, string issue, string suggestion, string severity, string code = "")
        {
            var si = new SceneIssue();
            si.code = code;
            si.componentType = componentType;
            si.hierarchyPath = c == null ? "(场景级)" : HierarchyPath(c);
            si.issue = issue;
            si.detail = c == null ? "" : c.GetType().Name;
            si.suggestion = suggestion;
            si.severity = severity;
            s.sceneIssues.Add(si);
        }

        public static string HierarchyPath(Component c)
        {
            if (c == null) return "";
            var sb = new StringBuilder(c.gameObject.name);
            var t = c.transform.parent;
            while (t != null)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            return sb.ToString();
        }

        // ---------------- 场景对象枚举 ----------------

        /// <summary>
        /// 用 Resources.FindObjectsOfTypeAll 而非 FindObjectsOfType：
        /// 后者在 Unity 6 已标记 Obsolete（会产生编译警告），且无法拿到未激活对象。
        /// </summary>
        public static List<T> SceneObjects<T>() where T : Component
        {
            var result = new List<T>();
            var objs = Resources.FindObjectsOfTypeAll(typeof(T));
            for (int i = 0; i < objs.Length; i++)
            {
                var o = objs[i] as T;
                if (o == null) continue;
                if (EditorUtility.IsPersistent(o)) continue;      // 排除工程资源（prefab/asset）
                if (o.gameObject == null) continue;
                if (!o.gameObject.scene.IsValid()) continue;      // 排除 preview 场景
                result.Add(o);
            }
            return result;
        }

        public static List<Component> SceneObjects(Type type)
        {
            var result = new List<Component>();
            var objs = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < objs.Length; i++)
            {
                var o = objs[i] as Component;
                if (o == null) continue;
                if (EditorUtility.IsPersistent(o)) continue;
                if (o.gameObject == null) continue;
                if (!o.gameObject.scene.IsValid()) continue;
                result.Add(o);
            }
            return result;
        }
    }

    /// <summary>物理配置审计（可与场景审计分开单独跑）。</summary>
    public class PhysicsAuditCollector : IPerfCollector
    {
        public string Name { get { return "物理配置"; } }
        public string ToolName { get { return "audit_physics"; } }
        public string Description { get { return "物理设置与碰撞体规模审计（Auto Sync Transforms、fixedDeltaTime、MeshCollider 等）。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            var colliders = SceneAuditCollector.SceneObjects<Collider>();
            var rigidbodies = SceneAuditCollector.SceneObjects<Rigidbody>();

            int meshColliders = 0, nonConvex = 0;
            for (int i = 0; i < colliders.Count; i++)
            {
                var mc = colliders[i] as MeshCollider;
                if (mc == null) continue;
                meshColliders++;
                if (!mc.convex) nonConvex++;
            }

            s.SetMetric("Collider", "个", colliders.Count, "场景内");
            s.SetMetric("Rigidbody", "个", rigidbodies.Count, "场景内");
            s.SetMetric("MeshCollider", "个", meshColliders, "场景内");

            if (s.physicsAutoSyncTransforms)
                AddIssue(s, "Physics", "Physics.autoSyncTransforms = true，Transform 变更会立即同步物理",
                    "关闭 Auto Sync Transforms", Severity.Error, "(场景级)", "scene_autosync_transforms");

            if (s.physicsSimulationMode != "FixedUpdate" && !string.IsNullOrEmpty(s.physicsSimulationMode))
                AddIssue(s, "Physics", "Physics.simulationMode = " + s.physicsSimulationMode, "保持 FixedUpdate 模式", Severity.Info, "(场景级)", "scene_physics_simulation_mode");

            if (s.fixedDeltaTime > 0 && s.fixedDeltaTime < 0.01)
                AddIssue(s, "Physics", string.Format(CultureInfo.InvariantCulture, "Time.fixedDeltaTime = {0} 过密", s.fixedDeltaTime),
                    "保持 0.02", Severity.Warn, "(场景级)", "scene_fixed_delta_too_small");

            if (meshColliders > 0)
                AddIssue(s, "MeshCollider", string.Format(CultureInfo.InvariantCulture, "{0} 个 MeshCollider（其中非凸 {1} 个）", meshColliders, nonConvex),
                    "静态用非凸，动态用凸包或基础碰撞体，也可用 Layers 减少碰撞对", Severity.Info, "(场景级)", "scene_mesh_collider_non_convex");

            if (colliders.Count > 0 && rigidbodies.Count > 0 && colliders.Count > rigidbodies.Count * 10 && rigidbodies.Count > 20)
                AddIssue(s, "Physics", string.Format(CultureInfo.InvariantCulture, "碰撞体/刚体比例 {0}:1，宽相检测压力大", colliders.Count / Mathf.Max(1, rigidbodies.Count)),
                    "精简复合碰撞体，用 Layer Collision Matrix 剔除不可能相交的组合", Severity.Warn, "(场景级)", "scene_collider_rigidbody_ratio");
        }

        static void AddIssue(PerfSnapshot s, string type, string issue, string suggestion, string severity, string path, string code = "")
        {
            var si = new SceneIssue();
            si.code = code;
            si.componentType = type;
            si.hierarchyPath = path;
            si.issue = issue;
            si.suggestion = suggestion;
            si.severity = severity;
            s.sceneIssues.Add(si);
        }
    }
}

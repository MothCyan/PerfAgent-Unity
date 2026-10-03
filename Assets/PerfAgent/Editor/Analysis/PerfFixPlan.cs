using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 一键修复的动作 id。
    ///
    /// 规划器只产出 id，真正的执行在 PerfFixExecutor 里完成 —— 这样规划逻辑
    /// （纯数据、可回归）与改工程的动作（强依赖 Editor API）彻底分开，互不牵制。
    /// </summary>
    public static class FixActionIds
    {
        // 纹理导入设置
        public const string TextureReadWriteOff = "texture.readwrite.off";
        public const string TextureCompress = "texture.compress.on";
        public const string TextureMaxSizeReduce = "texture.maxsize.reduce";
        public const string TextureStreamingOn = "texture.streaming.on";

        // 模型导入设置
        public const string ModelReadWriteOff = "model.readwrite.off";
        public const string ModelColliderOff = "model.collider.off";
        public const string ModelBlendShapesOff = "model.blendshapes.off";
        public const string ModelMeshCompression = "model.meshcompression.on";

        // 音频导入设置
        public const string AudioLoadTypeFix = "audio.loadtype.fix";
        public const string AudioPreloadOff = "audio.preload.off";

        // 工程设置
        public const string PhysicsAutoSyncOff = "physics.autosync.off";
        public const string PhysicsFixedDeltaReset = "physics.fixeddeltatime.reset";

        // 场景对象
        public const string SceneSkinnedOffscreenOff = "scene.skinned.offscreen.off";
        public const string SceneMarkStatic = "scene.markstatic.on";

        // 场景对象（第二批：粒子 / 灯光 / 相机 / 碰撞体 / 物理）
        public const string SceneLightHardShadow = "scene.light.hardshadow";
        public const string SceneCameraDepthOnly = "scene.camera.depthonly";
        public const string SceneParticleMaxReduce = "scene.particle.maxreduce";
        public const string SceneParticleLocal = "scene.particle.local";
        public const string SceneMeshColliderConvex = "scene.meshcollider.convex";
        public const string PhysicsSimulationFixed = "physics.simulationmode.fixed";

        // 图集（降低 Draw Call / SetPass）
        public const string AtlasCreateSprite = "atlas.create.sprite";
    }

    /// <summary>修复风险：决定按钮颜色、是否需要二次确认、能否被「一键执行低风险项」批量执行。</summary>
    public static class FixRisk
    {
        public const string Safe = "safe";         // 只影响内存/开销，不改资源表现与游戏逻辑
        public const string Moderate = "moderate"; // 会改变资源导入结果或运行时行为
        public const string Risky = "risky";       // 可能影响玩法/表现（碰撞体、形变、静态标记）

        public static int Rank(string risk)
        {
            if (risk == Risky) return 3;
            if (risk == Moderate) return 2;
            return 1;
        }

        public static string Label(string risk)
        {
            if (risk == Risky) return "高风险";
            if (risk == Moderate) return "中风险";
            return "低风险";
        }
    }

    /// <summary>步骤类型：决定按钮该怎么画、点击后该走哪条路。</summary>
    public static class FixKind
    {
        public const string ImportSetting = "import_setting"; // 改 AssetImporter 并重新导入
        public const string ProjectSetting = "project_setting"; // 改 ProjectSettings
        public const string SceneObject = "scene_object";       // 改场景对象（可 Undo）
        public const string AssetCreate = "asset_create";       // 新建资产（例如生成 SpriteAtlas）
        public const string Navigate = "navigate";              // 只跳转，不改任何东西
        public const string Manual = "manual";                  // 只能人工做，没有执行按钮

        public static bool IsExecutable(string kind)
        {
            return kind == ImportSetting || kind == ProjectSetting || kind == SceneObject
                || kind == AssetCreate;
        }
    }

    /// <summary>修复计划里的一步。一步 = 一个可执行动作（批量时作用于多个目标）。</summary>
    public class PerfFixStep
    {
        public string actionId = "";
        public string kind = FixKind.Manual;
        public string risk = FixRisk.Moderate;
        public string title = "";
        /// <summary>具体会改什么（执行确认框直接展示这段）。</summary>
        public string detail = "";
        public string expectedGain = "";
        public bool reversible;
        /// <summary>是否批量作用于多个目标。</summary>
        public bool batch;
        /// <summary>受影响的资源路径 / 场景对象路径 / 文件:行。</summary>
        public List<string> targets = new List<string>();
        /// <summary>真实受影响总数（targets 只保留前若干个用于展示）。</summary>
        public int targetCount;

        public bool CanExecute { get { return FixKind.IsExecutable(kind) && !string.IsNullOrEmpty(actionId); } }
        public bool IsSafe { get { return risk == FixRisk.Safe; } }

        public string TargetSummary()
        {
            if (targetCount <= 0) return "";
            if (!batch) return targets.Count > 0 ? targets[0] : "";
            return string.Format(CultureInfo.InvariantCulture, "{0} 个目标", targetCount);
        }
    }

    /// <summary>一条结论对应的一键修复计划。</summary>
    public class PerfFixPlan
    {
        public string findingId = "";
        public string category = "";
        public string severity = Severity.Info;
        public string title = "";
        public string diagnosis = "";
        public string expectedGain = "";
        public List<PerfFixStep> steps = new List<PerfFixStep>();

        public int ExecutableStepCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < steps.Count; i++) if (steps[i].CanExecute) n++;
                return n;
            }
        }

        public int SafeStepCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < steps.Count; i++) if (steps[i].CanExecute && steps[i].IsSafe) n++;
                return n;
            }
        }

        public bool HasExecutable { get { return ExecutableStepCount > 0; } }

        public string Summary()
        {
            if (steps.Count == 0) return "暂无自动修复项，需人工处理。";
            int exec = ExecutableStepCount;
            if (exec == 0) return steps.Count + " 条人工步骤";
            return string.Format(CultureInfo.InvariantCulture, "{0} 条可一键执行 / 共 {1} 条", exec, steps.Count);
        }
    }

    /// <summary>
    /// 修复计划规划器：把「结论」翻译成「可执行动作 + 人工步骤」，并按收益/成本排序。
    ///
    /// 设计纪律：
    ///  1. 只产出计划，不动任何工程文件 —— 执行必须由用户点按钮触发；
    ///  2. 每个可执行步骤都必须能追溯到结论 id 与证据（不凭经验臆造动作）；
    ///  3. 分不清风险的一律标 Risky 或不给执行按钮，绝不让用户「一键」改坏东西。
    ///
    /// 纯逻辑、不依赖 UnityEngine，因此可被独立回归与 Unity EditMode 测试同时覆盖。
    /// </summary>
    public static class PerfFixPlanner
    {
        /// <summary>展示上限（避免把上千个资源塞进 UI 与确认框）。</summary>
        public const int MaxTargetsShown = 20;

        public static List<PerfFixPlan> BuildPlans(PerfSnapshot snapshot)
        {
            var plans = new List<PerfFixPlan>();
            if (snapshot == null || snapshot.findings == null) return plans;

            for (int i = 0; i < snapshot.findings.Count; i++)
            {
                var plan = BuildPlan(snapshot, snapshot.findings[i]);
                if (plan != null) plans.Add(plan);
            }

            plans.Sort(Compare);
            return plans;
        }

        /// <summary>收益/成本排序：严重度 → 有可执行项 → 风险低 → 影响面大。</summary>
        static int Compare(PerfFixPlan a, PerfFixPlan b)
        {
            int bySeverity = Severity.Rank(b.severity).CompareTo(Severity.Rank(a.severity));
            if (bySeverity != 0) return bySeverity;

            if (a.HasExecutable != b.HasExecutable) return a.HasExecutable ? -1 : 1;

            int riskA = MinRisk(a), riskB = MinRisk(b);
            if (riskA != riskB) return riskA.CompareTo(riskB);

            return TargetCount(b).CompareTo(TargetCount(a));
        }

        static int MinRisk(PerfFixPlan plan)
        {
            int min = 4;
            for (int i = 0; i < plan.steps.Count; i++)
            {
                if (!plan.steps[i].CanExecute) continue;
                int r = FixRisk.Rank(plan.steps[i].risk);
                if (r < min) min = r;
            }
            return min;
        }

        static int TargetCount(PerfFixPlan plan)
        {
            int n = 0;
            for (int i = 0; i < plan.steps.Count; i++) n += plan.steps[i].targetCount;
            return n;
        }

        public static PerfFixPlan BuildPlan(PerfSnapshot snapshot, PerfFinding finding)
        {
            if (finding == null) return null;

            var plan = new PerfFixPlan();
            plan.findingId = finding.id;
            plan.category = finding.category;
            plan.severity = finding.severity;
            plan.title = finding.title;
            plan.diagnosis = finding.detail;

            // 带 fixCode 的结论（资源 / 场景审计类）走精确映射
            if (!string.IsNullOrEmpty(finding.fixCode))
            {
                AddAuditSteps(snapshot, finding, plan);
                if (plan.steps.Count > 0) return plan;
            }

            AddRuleSteps(snapshot, finding, plan);
            return plan.steps.Count > 0 ? plan : null;
        }

        // =====================================================================
        // 审计类结论 → 精确的一键修复动作
        // =====================================================================

        static void AddAuditSteps(PerfSnapshot snapshot, PerfFinding finding, PerfFixPlan plan)
        {
            switch (finding.fixCode)
            {
                case "texture_readwrite":
                    AddBatch(plan, FixActionIds.TextureReadWriteOff, FixKind.ImportSetting, FixRisk.Safe,
                        "关闭这些纹理的 Read/Write", "关闭 TextureImporter.isReadable 并重新导入",
                        "释放一份 CPU 可读副本，纹理内存约减半",
                        true, TextureTargets(snapshot, "texture_readwrite"));
                    break;

                case "texture_uncompressed":
                    AddBatch(plan, FixActionIds.TextureCompress, FixKind.ImportSetting, FixRisk.Moderate,
                        "改为压缩格式", "把 TextureImporter.textureCompression 从 Uncompressed 改为 Compressed",
                        "显存占用通常降到 1/4 ~ 1/8", true, TextureTargets(snapshot, "texture_uncompressed"));
                    break;

                case "texture_max_size":
                    AddBatch(plan, FixActionIds.TextureMaxSizeReduce, FixKind.ImportSetting, FixRisk.Moderate,
                        "把 maxTextureSize 降到 2048", "超过 2048 的按 2048 重新导入",
                        "显存按面积比例下降", true, TextureTargets(snapshot, "texture_max_size"));
                    break;

                case "texture_no_streaming":
                    AddBatch(plan, FixActionIds.TextureStreamingOn, FixKind.ImportSetting, FixRisk.Safe,
                        "开启 Mipmap Streaming", "打开 TextureImporter.streamingMipmaps",
                        "只让当前需要的 mip 常驻显存", true, TextureTargets(snapshot, "texture_no_streaming"));
                    break;

                case "model_readwrite":
                    AddBatch(plan, FixActionIds.ModelReadWriteOff, FixKind.ImportSetting, FixRisk.Moderate,
                        "关闭模型 Read/Write", "关闭 ModelImporter.isReadable",
                        "网格不再保留托管副本",
                        true, Paths(snapshot, "model_readwrite", delegate (AssetIssue a) { return a.path; }));
                    break;

                case "model_auto_collider":
                    AddBatch(plan, FixActionIds.ModelColliderOff, FixKind.ImportSetting, FixRisk.Risky,
                        "关闭自动生成碰撞体", "关闭 ModelImporter.addCollider",
                        "移除高开销的 MeshCollider",
                        true, Paths(snapshot, "model_auto_collider", delegate (AssetIssue a) { return a.path; }));
                    AddManual(plan, "改为手工挂基础碰撞体", "关闭自动碰撞体后，确认这些物体仍有 Box/Sphere/Capsule 碰撞体，否则物理会失效。");
                    break;

                case "model_blendshapes":
                    AddBatch(plan, FixActionIds.ModelBlendShapesOff, FixKind.ImportSetting, FixRisk.Risky,
                        "关闭 BlendShapes 导入", "关闭 ModelImporter.importBlendShapes",
                        "去掉顶点动画数据的内存占用",
                        true, Paths(snapshot, "model_blendshapes", delegate (AssetIssue a) { return a.path; }));
                    AddManual(plan, "确认没有表情/形变动画", "若角色依赖 BlendShape 表情，关闭后表情会失效。");
                    break;

                case "model_mesh_compression_off":
                    AddBatch(plan, FixActionIds.ModelMeshCompression, FixKind.ImportSetting, FixRisk.Moderate,
                        "开启网格压缩（Medium）", "ModelImporter.meshCompression = Medium",
                        "降低包体与加载耗时", true, Paths(snapshot, "model_mesh_compression_off", delegate (AssetIssue a) { return a.path; }));
                    break;

                case "audio_decompress_on_load":
                    AddBatch(plan, FixActionIds.AudioLoadTypeFix, FixKind.ImportSetting, FixRisk.Moderate,
                        "修正音频加载方式", "大文件改 Streaming，其余改 CompressedInMemory",
                        "不再解码后常驻内存", true, Paths(snapshot, "audio_decompress_on_load", delegate (AssetIssue a) { return a.path; }));
                    break;

                case "audio_preload":
                    AddBatch(plan, FixActionIds.AudioPreloadOff, FixKind.ImportSetting, FixRisk.Moderate,
                        "关闭 Preload Audio Data", "关闭 preloadAudioData，改为按需加载",
                        "加载时不再立即占用内存", true, Paths(snapshot, "audio_preload", delegate (AssetIssue a) { return a.path; }));
                    AddManual(plan, "确认首次播放不会卡顿", "关闭预加载后首次播放需要现解码，建议用音频池预热。");
                    break;

                case "resources_asset":
                    AddManual(plan, "把资源移出 Resources 目录", "Resources 下的一切都会被无条件打进包体并在启动时可被加载。改用 Addressables / AssetBundle，或直接改为场景/prefab 引用。");
                    AddManual(plan, "评估是否随包发布", "确认这些资源是否真的必须在首包内。");
                    break;

                case "asset_large":
                    AddManual(plan, "降低规格或改为下载内容", "大文件会拉长首包与安装体积，考虑压缩、降规格或移到远端下载。");
                    break;

                // ---------------- 场景 ----------------

                case "scene_autosync_transforms":
                    AddProject(plan, FixActionIds.PhysicsAutoSyncOff, FixRisk.Safe,
                        "关闭 Physics.autoSyncTransforms",
                        "把 ProjectSettings 里的 Auto Sync Transforms 关掉",
                        "消除每次 Transform 变更引发的物理同步开销",
                        true);
                    AddManual(plan, "需要时手动同步", "关闭后，代码里改了 Transform 又要立刻做射线检测的地方，需要显式调用 Physics.SyncTransforms()。");
                    break;

                case "scene_fixed_delta_too_small":
                    AddProject(plan, FixActionIds.PhysicsFixedDeltaReset, FixRisk.Moderate,
                        "把 fixedDeltaTime 调回 0.02（50Hz）",
                        "Time.fixedDeltaTime = 0.02",
                        "物理步进次数回到合理范围",
                        true);
                    break;

                case "scene_skinned_offscreen":
                    AddSceneBatch(plan, FixActionIds.SceneSkinnedOffscreenOff, FixRisk.Moderate,
                        "关闭 updateWhenOffscreen",
                        "把这些 SkinnedMeshRenderer 的 updateWhenOffscreen 设为 false",
                        "视野外的角色不再持续做蒙皮计算",
                        true, SceneTargets(snapshot, "scene_skinned_offscreen"));
                    AddManual(plan, "检查包围盒", "关闭后若包围盒不准，角色会在边缘被错误剔除，需要修正 Bounds。");
                    break;

                case "scene_non_static_renderers":
                    AddSceneBatch(plan, FixActionIds.SceneMarkStatic, FixRisk.Risky,
                        "把不移动的渲染器标记为 Static",
                        "对勾选范围内的 MeshRenderer 所在 GameObject 设置 Batching Static / Contribute GI",
                        "让静态批处理生效，显著降低 Draw Call",
                        true, MeshRendererTargets(snapshot));
                    AddManual(plan, "先人工核对列表", "标记 Static 会影响光照烘焙与遮挡剔除，务必只对确定不动的物体执行。");
                    break;

                case "scene_audio_source_count":
                    AddManual(plan, "复用少量 AudioSource", "改成音频池（10~20 个 AudioSource 轮转播放），而不是每个音效挂一个源。");
                    break;

                case "scene_canvas_count":
                    AddManual(plan, "合并 Canvas 或按更新频率拆分", "同一 Canvas 内的 UI 变化会触发整块重建；把高频变化的区域拆到独立 Canvas 反而更好。");
                    break;

                case "scene_camera_skybox":
                    AddSceneScan(plan, FixActionIds.SceneCameraDepthOnly, FixRisk.Moderate,
                        "副相机改为 Depth Only",
                        "把场景里非 MainCamera 的相机 Clear Flags 设为 Depth Only（主相机不动）",
                        "避免重复清屏与重绘天空盒",
                        SceneTargets(snapshot, "scene_camera_skybox"));
                    AddManual(plan, "确认天空盒只由主相机绘制", "若确实需要相机叠加渲染天空盒，保留即可。");
                    break;

                case "scene_light_soft_shadow":
                    AddSceneScan(plan, FixActionIds.SceneLightHardShadow, FixRisk.Moderate,
                        "点光 / 聚光改为硬阴影",
                        "把非方向光的 Light.shadows 从 Soft 改为 Hard（方向光不动）",
                        "省掉阴影贴图的滤波开销",
                        SceneTargets(snapshot, "scene_light_soft_shadow"));
                    AddManual(plan, "评估观感影响", "硬阴影边缘更硬；对静态场景建议直接改为烘焙。");
                    break;

                case "scene_particle_max":
                    AddSceneScan(plan, FixActionIds.SceneParticleMaxReduce, FixRisk.Moderate,
                        "降低粒子上限到 500",
                        "把 maxParticles 超过 500 的 ParticleSystem 降下来",
                        "减少半透明 Overdraw，这是移动端最常见的中低端机瓶颈",
                        SceneTargets(snapshot, "scene_particle_max"));
                    AddManual(plan, "检查特效表现", "降上限后密集发射的特效会提前停顿，必要时改为错峰发射。");
                    break;

                case "scene_particle_world_space":
                    AddSceneScan(plan, FixActionIds.SceneParticleLocal, FixRisk.Risky,
                        "粒子改为 Local 模拟空间",
                        "把 main.simulationSpace 从 World 改为 Local",
                        "避免粒子位置每帧在世界空间重算",
                        SceneTargets(snapshot, "scene_particle_world_space"));
                    AddManual(plan, "确认粒子要跟随物体", "改成 Local 后粒子会随物体移动，拖尾类特效会变样。");
                    break;

                case "scene_physics_simulation_mode":
                    AddProject(plan, FixActionIds.PhysicsSimulationFixed, FixRisk.Moderate,
                        "物理改回 FixedUpdate 模式",
                        "把 Physics.simulationMode 设为 FixedUpdate",
                        "固定步长下物理开销可预测且可控制",
                        true);
                    AddManual(plan, "确认没有依赖手动驱动物理", "确定性回放 / 帧同步类项目需要手动驱动物理，这类项目请保留原设置。");
                    break;

                case "scene_mesh_collider_non_convex":
                    AddSceneScan(plan, FixActionIds.SceneMeshColliderConvex, FixRisk.Risky,
                        "动态物体的非凸碰撞体改为凸包",
                        "把挂着 Rigidbody 的 MeshCollider.convex 设为 true（静态物体不动）",
                        "凸包的构建与查询成本都远低于非凸",
                        SceneTargets(snapshot, "scene_mesh_collider_non_convex"));
                    AddManual(plan, "验证碰撞手感", "凸包会填平凹处，角色可能卡在原本可以进入的区域。");
                    break;

                case "scene_collider_rigidbody_ratio":
                    AddManual(plan, "精简复合碰撞体", "用 Layer Collision Matrix 剔除不可能相交的层组合，能直接降低宽相检测成本。");
                    break;
            }
        }

        // =====================================================================
        // 规则类结论 → 行动清单（大多是人工步骤 + 导航）
        // =====================================================================

        static void AddRuleSteps(PerfSnapshot snapshot, PerfFinding finding, PerfFixPlan plan)
        {
            switch (finding.id)
            {
                case "gc_alloc_per_frame":
                case "gc_alloc_nonzero":
                case "gc_frequency":
                case "marker_gc_alloc":
                    AddCodeNavigation(snapshot, plan, "跳到每帧分配点");
                    AddManual(plan, "消除每帧分配", "逐个处理扫描出的位置：容器改为复用字段、字符串改用 StringBuilder/缓存、避免 LINQ 与闭包装箱。");
                    AddManual(plan, "不要用 GC.Collect 兜底", "手动触发 GC 只会把卡顿集中到调用点，治标不治本。");
                    break;

                case "temp_allocator_over":
                case "temp_allocator_growth":
                    AddManual(plan, "排查原生内存生命周期", "检查 NativeArray/NativeList 是否都有 Dispose、JobHandle 是否 Complete、是否漏了 using。");
                    AddManual(plan, "排查第三方原生插件", "音视频、网络、加密类插件常持有原生内存，逐个禁用确认。");
                    break;

                case "memory_total_over":
                case "texture_memory_over":
                    AddTextureMemorySteps(snapshot, plan);
                    break;

                case "frame_time_over":
                case "frame_time_near_budget":
                case "frame_time_jitter":
                case "spike_attribution":
                    AddManual(plan, "先定位耗时集中的模块", "看「Marker」标签的自身耗时排行，优先处理占比最高的那个。");
                    AddManual(plan, "再区分稳态慢还是尖峰卡", "稳态慢看渲染统计（Draw Call / SetPass）；尖峰卡看帧标签里的分配与 GC。");
                    break;

                case "marker_hotspot":
                    AddManual(plan, "把热点收敛到具体脚本", "根据 Marker 名称定位模块，做算法优化或降低调用频率（分帧、缓存、事件驱动）。");
                    break;

                case "draw_calls_over":
                    AddAtlasStep(plan);
                    AddManual(plan, "先确认合批断裂原因", "在 Frame Debugger 里看是哪一步打断了合批：材质不同、渲染队列不同，还是被阴影/深度 Pass 隔开。");
                    AddManual(plan, "共享材质", "把用同一材质的小物件合并到一次绘制，尽量用 SRP Batcher 兼容的 shader。");
                    break;

                case "setpass_over":
                    AddAtlasStep(plan);
                    AddManual(plan, "减少材质与 Shader 变体切换", "按材质排序渲染、合并 Shader 变体、用 MaterialPropertyBlock 代替多材质实例。");
                    break;

                case "triangles_over":
                    AddManual(plan, "引入 LOD 与遮挡剔除", "给远处物体配置 LOD Group，并烘焙 Occlusion Culling 数据。");
                    break;

                case "physics_meshcollider":
                    AddManual(plan, "确认 MeshCollider 使用是否合理", "静态物体保留非凸；动态物体改成凸包或 Box/Sphere/Capsule 组合。");
                    break;

                case "config_no_framelimit":
                    AddManual(plan, "测试时锁定帧率", "设置 Application.targetFrameRate = 目标帧率，或开启 VSync，否则测得的帧耗时不能代表目标设备。");
                    break;

                default:
                    if (finding.id != null && finding.id.StartsWith("code_", StringComparison.Ordinal))
                    {
                        AddCodeSteps(snapshot, finding, plan);
                    }
                    else
                    {
                        AddManual(plan, finding.title, string.IsNullOrEmpty(finding.recommendation)
                            ? "该结论没有自动修复方案，请按证据链人工处理。"
                            : finding.recommendation);
                    }
                    break;
            }
        }

        /// <summary>纹理内存类的结论：把当前快照里所有纹理问题都变成可执行步骤。</summary>
        /// <summary>带模式过滤的代码导航：只列当前结论对应的那种问题，不把全部代码问题混进来。</summary>
        static void AddCodeNavigation(PerfSnapshot snapshot, PerfFixPlan plan, string title, string pattern)
        {
            if (snapshot.codeIssues == null || snapshot.codeIssues.Count == 0) return;

            int hitCount = 0;
            for (int i = 0; i < snapshot.codeIssues.Count; i++)
                if (CodeIssue.BasePattern(snapshot.codeIssues[i].pattern) == pattern) hitCount++;
            if (hitCount == 0) return;

            var step = new PerfFixStep();
            step.actionId = "";
            step.kind = FixKind.Navigate;
            step.risk = FixRisk.Safe;
            step.title = title;
            step.detail = "打开第一处（该模式共 " + hitCount + " 处）";
            step.expectedGain = "直接看到代码，省去手工搜索";
            step.batch = true;
            step.targetCount = hitCount;

            int shown = 0;
            for (int i = 0; i < snapshot.codeIssues.Count && shown < MaxTargetsShown; i++)
            {
                var c = snapshot.codeIssues[i];
                if (CodeIssue.BasePattern(c.pattern) != pattern) continue;
                step.targets.Add(c.file + ":" + c.line);
                shown++;
            }

            plan.steps.Add(step);
        }

        /// <summary>
        /// 代码类结论的修复计划。
        ///
        /// 只产出「跳到问题位置」+ 人工步骤 —— 本插件不提供改写脚本源码的能力：
        /// 自动改代码要理解上下文，错一行就是编译不过或运行时崩，风险远大于收益。
        /// 具体的改法靠规则引擎的建议与 LLM 对话，由使用者自己动手。
        /// </summary>
        static void AddCodeSteps(PerfSnapshot snapshot, PerfFinding finding, PerfFixPlan plan)
        {
            // fixCode 由规则引擎写成纯反模式 id（如 "linq"）；
            // 旧快照里可能没有，那时回退到从 finding id 里解析
            string pattern = finding.fixCode;
            if (string.IsNullOrEmpty(pattern)) pattern = CodeIssue.BasePattern(finding.id);

            AddCodeNavigation(snapshot, plan, "跳到问题位置", pattern);

            AddManual(plan, finding.title, string.IsNullOrEmpty(finding.recommendation)
                ? "按证据链定位到该处代码后人工处理。"
                : finding.recommendation);
        }

        /// <summary>
        /// 图集动作。目前只收录 Sprite 贴图 —— 因为 Draw Call 的真正成因
        /// 必须跑 Frame Debugger 才能确定，静态分析猜不出来，
        /// 所以这里只做「确定有效的另一半」。
        /// </summary>
        static void AddAtlasStep(PerfFixPlan plan)
        {
            var step = new PerfFixStep();
            step.actionId = FixActionIds.AtlasCreateSprite;
            step.kind = FixKind.AssetCreate;
            step.risk = FixRisk.Safe;
            step.title = "生成 Sprite 图集";
            step.detail = "在 Assets 下新建 PerfAgentAtlas.spriteatlas，并收录工程内全部 Sprite 类型贴图";
            step.expectedGain = "同图集的 UI / 2D 元素具备合批条件，可降低 Draw Call 与 SetPass";
            step.reversible = true;
            step.batch = false;
            step.targetCount = 1;
            step.targets.Add("Assets");
            plan.steps.Add(step);
        }

        /// <summary>
        /// 场景级结论：审计只报了数量、没报具体对象（如「3 个非凸 MeshCollider」）。
        /// 这种情况 targets 留空，执行器会扫整个场景找符合条件的目标。
        /// </summary>
        static void AddSceneScan(PerfFixPlan plan, string actionId, string risk,
            string title, string detail, string gain, List<string> targets)
        {
            if (targets != null && targets.Count > 0)
            {
                AddSceneBatch(plan, actionId, risk, title, detail, gain, true, targets);
                return;
            }

            var step = new PerfFixStep();
            step.actionId = actionId;
            step.kind = FixKind.SceneObject;
            step.risk = risk;
            step.title = title;
            step.detail = detail + "（将扫描当前场景全部对象）";
            step.expectedGain = gain;
            step.reversible = true;
            step.batch = true;
            step.targetCount = 0;
            plan.steps.Add(step);
        }

        static void AddTextureMemorySteps(PerfSnapshot snapshot, PerfFixPlan plan)
        {
            AddAuditSteps(snapshot, new PerfFinding { fixCode = "texture_readwrite" }, plan);
            AddAuditSteps(snapshot, new PerfFinding { fixCode = "texture_uncompressed" }, plan);
            AddAuditSteps(snapshot, new PerfFinding { fixCode = "texture_max_size" }, plan);
            AddAuditSteps(snapshot, new PerfFinding { fixCode = "texture_no_streaming" }, plan);

            if (plan.steps.Count == 0)
                AddManual(plan, "先跑一次资源审计", "当前快照里没有纹理级明细，点「静态审计」补齐后再回来执行。");
        }

        static void AddCodeNavigation(PerfSnapshot snapshot, PerfFixPlan plan, string title)
        {
            if (snapshot.codeIssues == null || snapshot.codeIssues.Count == 0) return;

            var step = new PerfFixStep();
            step.actionId = "";
            step.kind = FixKind.Navigate;
            step.risk = FixRisk.Safe;
            step.title = title;
            step.detail = "打开第一个问题位置（共 " + snapshot.codeIssues.Count + " 处）";
            step.expectedGain = "直接看到代码，省去手工搜索";
            step.batch = true;
            step.targetCount = snapshot.codeIssues.Count;

            for (int i = 0; i < snapshot.codeIssues.Count && i < MaxTargetsShown; i++)
                step.targets.Add(snapshot.codeIssues[i].file + ":" + snapshot.codeIssues[i].line);

            plan.steps.Add(step);
        }

        // =====================================================================
        // 构造工具
        // =====================================================================

        static void AddBatch(PerfFixPlan plan, string actionId, string kind, string risk,
            string title, string detail, string gain, bool reversible, List<string> targets)
        {
            if (targets == null || targets.Count == 0) return;

            var step = new PerfFixStep();
            step.actionId = actionId;
            step.kind = kind;
            step.risk = risk;
            step.title = title;
            step.detail = detail;
            step.expectedGain = gain;
            step.reversible = reversible;
            step.batch = true;
            step.targetCount = targets.Count;
            for (int i = 0; i < targets.Count && i < MaxTargetsShown; i++) step.targets.Add(targets[i]);
            plan.steps.Add(step);
        }

        static void AddProject(PerfFixPlan plan, string actionId, string risk,
            string title, string detail, string gain, bool reversible)
        {
            var step = new PerfFixStep();
            step.actionId = actionId;
            step.kind = FixKind.ProjectSetting;
            step.risk = risk;
            step.title = title;
            step.detail = detail;
            step.expectedGain = gain;
            step.reversible = reversible;
            step.batch = false;
            step.targetCount = 1;
            step.targets.Add("ProjectSettings");
            plan.steps.Add(step);
        }

        static void AddSceneBatch(PerfFixPlan plan, string actionId, string risk,
            string title, string detail, string gain, bool reversible, List<string> targets)
        {
            if (targets == null || targets.Count == 0) return;

            var step = new PerfFixStep();
            step.actionId = actionId;
            step.kind = FixKind.SceneObject;
            step.risk = risk;
            step.title = title;
            step.detail = detail;
            step.expectedGain = gain;
            step.reversible = reversible;
            step.batch = true;
            step.targetCount = targets.Count;
            for (int i = 0; i < targets.Count && i < MaxTargetsShown; i++) step.targets.Add(targets[i]);
            plan.steps.Add(step);
        }

        static void AddManual(PerfFixPlan plan, string title, string detail)
        {
            var step = new PerfFixStep();
            step.kind = FixKind.Manual;
            step.risk = FixRisk.Moderate;
            step.title = title;
            step.detail = detail;
            plan.steps.Add(step);
        }

        // =====================================================================
        // 从快照里取目标
        // =====================================================================

        static List<string> TextureTargets(PerfSnapshot snapshot, string code)
        {
            return Paths(snapshot, code, delegate (AssetIssue a) { return a.path; });
        }

        static List<string> Paths(PerfSnapshot snapshot, string code, Func<AssetIssue, string> selector)
        {
            var result = new List<string>();
            if (snapshot.assetIssues == null) return result;

            for (int i = 0; i < snapshot.assetIssues.Count; i++)
            {
                var a = snapshot.assetIssues[i];
                if (a.code != code) continue;
                string value = selector(a);
                if (!string.IsNullOrEmpty(value) && !result.Contains(value)) result.Add(value);
            }
            return result;
        }

        static List<string> SceneTargets(PerfSnapshot snapshot, string code)
        {
            var result = new List<string>();
            if (snapshot.sceneIssues == null) return result;

            for (int i = 0; i < snapshot.sceneIssues.Count; i++)
            {
                var v = snapshot.sceneIssues[i];
                if (v.code != code) continue;
                if (!string.IsNullOrEmpty(v.hierarchyPath) && !result.Contains(v.hierarchyPath)) result.Add(v.hierarchyPath);
            }
            return result;
        }

        /// <summary>未参与批处理的渲染器：取所有非静态 MeshRenderer 的层级路径。</summary>
        static List<string> MeshRendererTargets(PerfSnapshot snapshot)
        {
            var result = new List<string>();
            if (snapshot.sceneIssues == null) return result;

            for (int i = 0; i < snapshot.sceneIssues.Count; i++)
            {
                var v = snapshot.sceneIssues[i];
                if (v.componentType != "MeshRenderer") continue;
                if (v.hierarchyPath == "(场景级)" || string.IsNullOrEmpty(v.hierarchyPath)) continue;
                if (!result.Contains(v.hierarchyPath)) result.Add(v.hierarchyPath);
            }

            // 场景级结论本身不带具体对象时，返回空 —— 由人工步骤兜底，绝不猜对象
            return result;
        }
    }
}

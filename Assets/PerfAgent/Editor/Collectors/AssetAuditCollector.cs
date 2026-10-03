using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Collectors
{
    /// <summary>
    /// 资源导入设置审计。
    /// 刻意不加载资源本体（只读 Importer 与文件大小），保证在大工程上也很快。
    /// </summary>
    public class AssetAuditCollector : IPerfCollector
    {
        public string Name { get { return "资源审计"; } }
        public string ToolName { get { return "audit_assets"; } }
        public string Description { get { return "扫描纹理/模型/音频/Shader 的导入设置问题与 Resources 目录滥用，给出可执行的修复建议。"; } }

        static readonly string[] TextureExt = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".exr", ".hdr", ".tif", ".tiff", ".bmp", ".gif" };
        static readonly string[] ModelExt = { ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb" };
        static readonly string[] AudioExt = { ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".aac", ".flac" };
        static readonly string[] ShaderExt = { ".shader", ".shadergraph", ".shadersubgraph", ".cginc", ".hlsl", ".compute" };

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            int topN = ctx.deep ? Mathf.Max(ctx.topN, 50) : ctx.topN;
            var textures = new List<string>();
            var models = new List<string>();
            var audios = new List<string>();
            var shaders = new List<string>();
            var resourcesAssets = new List<string>();
            var allFiles = new List<KeyValuePair<string, long>>();

            string[] paths;
            try { paths = AssetDatabase.GetAllAssetPaths(); }
            catch (Exception e)
            {
                ctx.Note("资源枚举失败: " + e.Message);
                return;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                if (path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0)
                    resourcesAssets.Add(path);

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (Contains(TextureExt, ext)) textures.Add(path);
                else if (Contains(ModelExt, ext)) models.Add(path);
                else if (Contains(AudioExt, ext)) audios.Add(path);
                else if (Contains(ShaderExt, ext)) shaders.Add(path);

                long size = FileSize(path);
                if (size > 256 * 1024) allFiles.Add(new KeyValuePair<string, long>(path, size));
            }

            s.SetMetric("资源总数", "个", paths.Length, "AssetDatabase.GetAllAssetPaths");
            s.SetMetric("纹理数", "个", textures.Count, "按扩展名统计");
            s.SetMetric("模型数", "个", models.Count, "按扩展名统计");
            s.SetMetric("音频数", "个", audios.Count, "按扩展名统计");
            s.SetMetric("Shader 数", "个", shaders.Count, "按扩展名统计");
            s.SetMetric("Resources 资源数", "个", resourcesAssets.Count, "路径含 /Resources/");

            long textureBytes = 0;
            textureBytes += AuditTextures(s, textures, topN);

            s.SetMetric("纹理内存(估算)", "B", textureBytes, "按导入设置估算（未加载资源本体）");

            AuditModels(s, models, topN);
            AuditAudio(s, audios, topN);
            AuditResources(s, resourcesAssets, topN);
            AuditLargeFiles(s, allFiles, topN);
        }

        static bool Contains(string[] arr, string v)
        {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == v) return true;
            return false;
        }

        static long FileSize(string assetPath)
        {
            try
            {
                var fi = new FileInfo(assetPath);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        static void Add(PerfSnapshot s, AssetIssue issue)
        {
            if (issue == null) return;
            s.assetIssues.Add(issue);
        }

        static AssetIssue New(string path, string type, string issue, string suggestion, string severity, string code = "")
        {
            var a = new AssetIssue();
            a.code = code;
            a.path = path; a.assetType = type; a.issue = issue; a.suggestion = suggestion; a.severity = severity;
            a.diskBytes = FileSize(path);
            return a;
        }

        // ---------------- 纹理 ----------------

        static long AuditTextures(PerfSnapshot s, List<string> textures, int topN)
        {
            long totalEstimates = 0;
            int reported = 0;

            // 先按磁盘大小排序，只审计最大的那批，避免大工程卡顿
            var sorted = new List<string>(textures);
            sorted.Sort((a, b) => FileSize(b).CompareTo(FileSize(a)));

            for (int i = 0; i < sorted.Count; i++)
            {
                string path = sorted[i];
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;

                int w = 0, h = 0;
                GetSourceSize(ti, out w, out h);
                if (w <= 0 || h <= 0) { w = 1024; h = 1024; }

                long est = EstimateTextureBytes(w, h, ti);
                totalEstimates += est;

                if (reported >= topN) continue;

                long disk = FileSize(path);

                if (ti.isReadable)
                {
                    var issue = New(path, "Texture", "开启 Read/Write，会在内存中保留一份 CPU 可读副本（内存翻倍）",
                        "关闭 TextureImporter.isReadable（除非确实需要 GetPixels/SetPixels）", Severity.Error, "texture_readwrite");
                    issue.estimatedMemoryBytes = est;
                    Add(s, issue); reported++; continue;
                }

                if (ti.textureCompression == TextureImporterCompression.Uncompressed && disk > 512 * 1024)
                {
                    var issue = New(path, "Texture", string.Format(CultureInfo.InvariantCulture, "未压缩纹理（{0}x{1}），显存占用约 {2:0.0} MB", w, h, est / 1048576.0),
                        "改为 Compressed（移动端优先 ASTC/ETC2），或降低 maxTextureSize", Severity.Error, "texture_uncompressed");
                    issue.estimatedMemoryBytes = est;
                    Add(s, issue); reported++; continue;
                }

                if (ti.maxTextureSize > 2048 && disk > 1024 * 1024)
                {
                    var issue = New(path, "Texture", string.Format(CultureInfo.InvariantCulture, "maxTextureSize={0} 偏大，显存约 {1:0.0} MB", ti.maxTextureSize, est / 1048576.0),
                        "按实际使用尺寸降到 1024/2048，并检查是否为 UI 贴图误用 mipmap", Severity.Warn, "texture_max_size");
                    issue.estimatedMemoryBytes = est;
                    Add(s, issue); reported++; continue;
                }

                if (est > 4 * 1024 * 1024 && !ti.streamingMipmaps)
                {
                    var issue = New(path, "Texture", string.Format(CultureInfo.InvariantCulture, "大纹理约 {0:0.0} MB 且未开启 Mipmap Streaming", est / 1048576.0),
                        "勾选 Streaming Mipmaps，并配置 Mipmap Streaming 预算", Severity.Warn, "texture_no_streaming");
                    issue.estimatedMemoryBytes = est;
                    Add(s, issue); reported++;
                }
            }

            if (sorted.Count > reported && reported >= topN)
                s.AddNote(string.Format(CultureInfo.InvariantCulture, "资源审计只报告了前 {0} 个纹理问题（共 {1} 个纹理），深度模式可扩大范围。", topN, sorted.Count));

            return totalEstimates;
        }

        static void GetSourceSize(TextureImporter ti, out int w, out int h)
        {
            w = 0; h = 0;
            var args = new object[] { 0, 0 };
            object ignored;
            if (Reflect.TryInvoke(ti, "GetSourceTextureWidthAndHeight", out ignored, args))
            {
                try { w = Convert.ToInt32(args[0]); h = Convert.ToInt32(args[1]); } catch { }
            }
        }

        /// <summary>按导入设置估算运行时显存占用（含 mipmap 1/3 增量）。</summary>
        static long EstimateTextureBytes(int w, int h, TextureImporter ti)
        {
            double bytesPerPixel;
            switch (ti.textureCompression)
            {
                case TextureImporterCompression.Uncompressed:
                    bytesPerPixel = 4.0; break;
                case TextureImporterCompression.CompressedHQ:
                    bytesPerPixel = 1.0; break;
                case TextureImporterCompression.CompressedLQ:
                    bytesPerPixel = 0.5; break;
                default:
                    bytesPerPixel = 0.5; break;
            }
            double baseBytes = w * (double)h * bytesPerPixel;
            if (ti.mipmapEnabled) baseBytes *= 1.3333;
            return (long)baseBytes;
        }

        // ---------------- 模型 ----------------

        static void AuditModels(PerfSnapshot s, List<string> models, int topN)
        {
            var sorted = new List<string>(models);
            sorted.Sort((a, b) => FileSize(b).CompareTo(FileSize(a)));

            int reported = 0;
            for (int i = 0; i < sorted.Count && reported < topN; i++)
            {
                string path = sorted[i];
                var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                if (mi == null) continue;

                if (mi.isReadable)
                {
                    Add(s, New(path, "Model", "开启 Read/Write，网格数据在内存中保留一份托管副本",
                        "关闭 ModelImporter.isReadable（需要动态合并网格时才开启）", Severity.Error, "model_readwrite"));
                    reported++; continue;
                }

                if (mi.addCollider)
                {
                    Add(s, New(path, "Model", "自动生成碰撞体（MeshCollider 属于高开销类型）",
                        "关闭 addCollider，改为手工挂 Box/Sphere/Capsule Collider", Severity.Warn, "model_auto_collider"));
                    reported++; continue;
                }

                if (mi.importBlendShapes && FileSize(path) > 4 * 1024 * 1024)
                {
                    Add(s, New(path, "Model", "导入 BlendShapes（顶点动画数据，显著增加内存）",
                        "若未使用表情/形变动画，关闭 importBlendShapes", Severity.Warn, "model_blendshapes"));
                    reported++; continue;
                }

                if (mi.meshCompression.ToString() == "Off" && FileSize(path) > 2 * 1024 * 1024)
                {
                    Add(s, New(path, "Model", "网格压缩为 Off，包体与加载耗时偏大",
                        "改为 Medium/High 网格压缩", Severity.Info, "model_mesh_compression_off"));
                    reported++;
                }
            }
        }

        // ---------------- 音频 ----------------

        static void AuditAudio(PerfSnapshot s, List<string> audios, int topN)
        {
            var sorted = new List<string>(audios);
            sorted.Sort((a, b) => FileSize(b).CompareTo(FileSize(a)));

            int reported = 0;
            for (int i = 0; i < sorted.Count && reported < topN; i++)
            {
                string path = sorted[i];
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (ai == null) continue;

                long size = FileSize(path);
                var st = ai.defaultSampleSettings;

                if (st.loadType == AudioClipLoadType.DecompressOnLoad && size > 1024 * 1024)
                {
                    Add(s, New(path, "Audio", "长音频使用 DecompressOnLoad，解码后常驻内存",
                        "改为 Streaming（BGM）或 CompressedInMemory（音效）", Severity.Error, "audio_decompress_on_load"));
                    reported++; continue;
                }

                if (IsPreloadEnabled(ai) && size > 2 * 1024 * 1024)
                {
                    Add(s, New(path, "Audio", "开启 Preload Audio Data，加载时即占用内存",
                        "关闭 preloadAudioData，改为按需加载", Severity.Warn, "audio_preload"));
                    reported++;
                }
            }
        }

        /// <summary>
        /// preloadAudioData 在 Unity 2022+ 已移到 defaultSampleSettings（原属性被标记为过时错误），
        /// 且字段名/位置随版本变化，因此这里走反射读取，任一版本都不会编译失败。
        /// </summary>
        static bool IsPreloadEnabled(AudioImporter ai)
        {
            object settings = ai.defaultSampleSettings;
            var v = Reflect.Get(settings, "preloadAudioData");
            return v is bool && (bool)v;
        }

        static void AuditResources(PerfSnapshot s, List<string> resourcesAssets, int topN)
        {
            if (resourcesAssets.Count == 0) return;

            var sorted = new List<string>(resourcesAssets);
            sorted.Sort((a, b) => FileSize(b).CompareTo(FileSize(a)));

            long total = 0;
            for (int i = 0; i < sorted.Count; i++) total += FileSize(sorted[i]);
            s.SetMetric("Resources 磁盘占用", "B", total, "路径含 /Resources/ 的资源合计");

            for (int i = 0; i < sorted.Count && i < topN; i++)
                Add(s, New(sorted[i], "Resources", "位于 Resources 目录，会被无条件打进包体并在启动时可被加载",
                    "改用 Addressables / AssetBundle，或把该资源移出 Resources", Severity.Warn, "resources_asset"));
        }

        // ---------------- 大文件 ----------------

        static void AuditLargeFiles(PerfSnapshot s, List<KeyValuePair<string, long>> files, int topN)
        {
            files.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < files.Count && i < topN / 2; i++)
            {
                var pair = files[i];
                var issue = New(pair.Key, "Large", string.Format(CultureInfo.InvariantCulture, "磁盘占用 {0:0.0} MB，进入包体后影响加载与首包体积", pair.Value / 1048576.0),
                    "确认是否必须随包发布；可考虑压缩、降规格或改为下载内容", Severity.Info, "asset_large");
                issue.diskBytes = pair.Value;
                Add(s, issue);
            }
        }
    }
}

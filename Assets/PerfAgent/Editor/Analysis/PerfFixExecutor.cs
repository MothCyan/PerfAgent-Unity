using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Analysis
{
    /// <summary>一次一键修复的执行结果。</summary>
    public class PerfFixOutcome
    {
        public bool success;
        public int changedCount;
        public int skippedCount;
        public string message = "";
        public List<string> details = new List<string>();
        public bool needsReanalyze;
        public string undoPayload = "";
        /// <summary>写入审计日志的记录（含 undoPayload），供 UI 直接提供「撤销」。</summary>
        public PerfFixRecord record;

        public string Describe()
        {
            if (!string.IsNullOrEmpty(message)) return message;
            return changedCount > 0 ? ("已修改 " + changedCount + " 项") : "没有需要修改的项。";
        }
    }

    /// <summary>
    /// 一键修复执行器。
    ///
    /// 纪律（这是整个工具里唯一会改工程文件的地方，必须最保守）：
    ///  1. **只做用户点按钮的动作**，永远不自动执行；
    ///  2. 每个动作都要把「改前的值」记录下来，写进审计日志，能撤销的都给撤销；
    ///  3. 找不到的目标、已经符合预期的目标一律跳过，不猜、不硬改；
    ///  4. 任何异常都收敛成结果消息，绝不留下改一半又崩掉的状态。
    /// </summary>
    public static partial class PerfFixExecutor
    {
        /// <summary>批量执行时逐个目标的进度回调（index, total, target）。</summary>
        public static Action<int, int, string> Progress;

        public static bool CanExecute(string actionId)
        {
            switch (actionId)
            {
                case FixActionIds.TextureReadWriteOff:
                case FixActionIds.TextureCompress:
                case FixActionIds.TextureMaxSizeReduce:
                case FixActionIds.TextureStreamingOn:
                case FixActionIds.ModelReadWriteOff:
                case FixActionIds.ModelColliderOff:
                case FixActionIds.ModelBlendShapesOff:
                case FixActionIds.ModelMeshCompression:
                case FixActionIds.AudioLoadTypeFix:
                case FixActionIds.AudioPreloadOff:
                case FixActionIds.PhysicsAutoSyncOff:
                case FixActionIds.PhysicsFixedDeltaReset:
                case FixActionIds.SceneSkinnedOffscreenOff:
                case FixActionIds.SceneMarkStatic:
                    return true;
                default:
                    return CanExecuteExtended(actionId);
            }
        }

        public static bool CanUndo(PerfFixRecord record)
        {
            return record != null && record.success && !record.undone && !string.IsNullOrEmpty(record.undoPayload);
        }

        // =====================================================================
        // 执行
        // =====================================================================

        /// <summary>
        /// 执行一个修复步骤。
        ///
        /// <paramref name="consent"/> 是**同意凭据**：
        ///   · 空 / null —— 人自己在面板里确认的（默认）；
        ///   · "mcp:&lt;client&gt;" —— 外部客户端经同意门批准后通过 MCP 触发（后期接入）。
        /// 它会被原样写进操作日志，事后能回答「这条改动是谁授意的」。
        /// </summary>
        public static PerfFixOutcome Execute(PerfFixStep step, string findingId, string consent = null)
        {
            var outcome = new PerfFixOutcome();
            if (step == null || !step.CanExecute)
            {
                outcome.message = "该步骤没有可执行动作。";
                return outcome;
            }

            var undo = new List<UndoItem>();

            try
            {
                switch (step.actionId)
                {
                    case FixActionIds.TextureReadWriteOff:
                        outcome = ApplyAssets(step, MutateTextureReadWrite, undo); break;
                    case FixActionIds.TextureCompress:
                        outcome = ApplyAssets(step, MutateTextureCompression, undo); break;
                    case FixActionIds.TextureMaxSizeReduce:
                        outcome = ApplyAssets(step, MutateTextureMaxSize, undo); break;
                    case FixActionIds.TextureStreamingOn:
                        outcome = ApplyAssets(step, MutateTextureStreaming, undo); break;

                    case FixActionIds.ModelReadWriteOff:
                        outcome = ApplyAssets(step, MutateModelReadWrite, undo); break;
                    case FixActionIds.ModelColliderOff:
                        outcome = ApplyAssets(step, MutateModelCollider, undo); break;
                    case FixActionIds.ModelBlendShapesOff:
                        outcome = ApplyAssets(step, MutateModelBlendShapes, undo); break;
                    case FixActionIds.ModelMeshCompression:
                        outcome = ApplyAssets(step, MutateModelMeshCompression, undo); break;

                    case FixActionIds.AudioLoadTypeFix:
                        outcome = ApplyAssets(step, MutateAudioLoadType, undo); break;
                    case FixActionIds.AudioPreloadOff:
                        outcome = ApplyAssets(step, MutateAudioPreload, undo); break;

                    case FixActionIds.PhysicsAutoSyncOff:
                        outcome = ApplyProjectSetting(step, undo, delegate (Dictionary<string, string> before)
                        {
                            if (!Physics.autoSyncTransforms) return false;
                            before["autoSyncTransforms"] = "1";
                            Physics.autoSyncTransforms = false;
                            AssetDatabase.SaveAssets();
                            return true;
                        }); break;

                    case FixActionIds.PhysicsFixedDeltaReset:
                        outcome = ApplyProjectSetting(step, undo, delegate (Dictionary<string, string> before)
                        {
                            if (Time.fixedDeltaTime >= 0.02f) return false;
                            before["fixedDeltaTime"] = Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture);
                            Time.fixedDeltaTime = 0.02f;
                            AssetDatabase.SaveAssets();
                            return true;
                        }); break;

                    case FixActionIds.SceneSkinnedOffscreenOff:
                        outcome = ApplySceneObjects(step, undo, MutateSkinnedOffscreen); break;
                    case FixActionIds.SceneMarkStatic:
                        outcome = ApplySceneObjects(step, undo, MutateMarkStatic); break;

                    default:
                        // 后加的动作一律走扩展部分，避免这个文件无限膨胀
                        outcome = ExecuteExtended(step, undo);
                        if (outcome == null)
                        {
                            outcome = new PerfFixOutcome();
                            outcome.message = "未注册的执行器：" + step.actionId;
                            return outcome;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                outcome.success = false;
                outcome.message = "执行异常：" + e.Message;
                Debug.LogException(e);
            }

            outcome.undoPayload = UndoItem.Build(undo);
            if (!outcome.success && outcome.changedCount > 0)
                outcome.success = true;   // 部分成功也算改了东西，允许撤销

            var record = new PerfFixRecord();
            record.actionId = step.actionId;
            record.title = step.title;
            record.kind = step.kind;
            record.risk = step.risk;
            record.findingId = findingId == null ? "" : findingId;
            record.success = outcome.success;
            record.changedCount = outcome.changedCount;
            record.message = outcome.Describe();
            record.undoPayload = outcome.undoPayload;
            PerfChangeLog.Append(record);
            outcome.record = record;

            // 操作日志：谁发起、谁同意、改了几项、能不能撤销
            PerfHistory.RecordOperation(ActorOf(consent), "fix", record.actionId, record.title,
                record.findingId, consent, record.success, record.changedCount, record.message,
                CanUndo(record));

            return outcome;
        }

        // =====================================================================
        // 撤销
        // =====================================================================

        /// <summary>撤销一次已记录的批量修改。仅对记录里带了还原信息的动作有效。</summary>
        public static PerfFixOutcome Revert(PerfFixRecord record, string consent = null)
        {
            var outcome = new PerfFixOutcome();
            if (!CanUndo(record))
            {
                outcome.message = "该记录没有可用的还原信息。";
                return outcome;
            }

            var items = UndoItem.Parse(record.undoPayload);
            if (items.Count == 0)
            {
                outcome.message = "还原信息为空。";
                return outcome;
            }

            try
            {
                switch (record.actionId)
                {
                    case FixActionIds.TextureReadWriteOff:
                    case FixActionIds.TextureCompress:
                    case FixActionIds.TextureMaxSizeReduce:
                    case FixActionIds.TextureStreamingOn:
                        UndoTexture(items, outcome);
                        break;

                    case FixActionIds.ModelReadWriteOff:
                    case FixActionIds.ModelColliderOff:
                    case FixActionIds.ModelBlendShapesOff:
                    case FixActionIds.ModelMeshCompression:
                        UndoModel(items, outcome);
                        break;

                    case FixActionIds.AudioLoadTypeFix:
                    case FixActionIds.AudioPreloadOff:
                        UndoAudio(items, outcome);
                        break;

                    case FixActionIds.PhysicsAutoSyncOff:
                    case FixActionIds.PhysicsFixedDeltaReset:
                        UndoProjectSetting(items, outcome);
                        break;

                    case FixActionIds.SceneSkinnedOffscreenOff:
                        UndoScene(items, outcome, "updateWhenOffscreen");
                        break;

                    case FixActionIds.SceneMarkStatic:
                        UndoScene(items, outcome, "staticFlags");
                        break;

                    default:
                        if (!RevertExtended(record, items, outcome))
                        {
                            outcome.message = "该动作不支持撤销。";
                            return outcome;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                outcome.success = false;
                outcome.message = "撤销异常：" + e.Message;
                Debug.LogException(e);
                return outcome;
            }

            outcome.success = outcome.changedCount > 0;
            outcome.message = outcome.changedCount > 0
                ? ("已还原 " + outcome.changedCount + " 项")
                : "没有需要还原的项（可能已被手动改回）。";
            outcome.needsReanalyze = outcome.success;
            PerfChangeLog.MarkUndone(record);

            PerfHistory.RecordOperation(ActorOf(consent), "undo", record.actionId, record.title,
                record.findingId, consent, outcome.success, outcome.changedCount, outcome.message, false);

            return outcome;
        }

        /// <summary>
        /// 从同意凭据推出发起方，写进操作日志的 `actor` 列。
        /// 这里刻意不做「根据来源放宽门禁」之类的事 —— 它只影响日志，不影响权限。
        /// </summary>
        static string ActorOf(string consent)
        {
            if (string.IsNullOrEmpty(consent)) return "human";
            return consent.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase) ? "mcp" : "ai";
        }

        // =====================================================================
        // 批量执行骨架
        // =====================================================================

        delegate bool AssetMutator(string path, Dictionary<string, string> before);

        static PerfFixOutcome ApplyAssets(PerfFixStep step, AssetMutator mutate, List<UndoItem> undo)
        {
            var outcome = new PerfFixOutcome();
            int total = step.targets.Count;

            for (int i = 0; i < total; i++)
            {
                string path = step.targets[i];
                if (Progress != null) Progress(i, total, path);

                var before = new Dictionary<string, string>();
                bool changed;
                try { changed = mutate(path, before); }
                catch (Exception e)
                {
                    outcome.skippedCount++;
                    outcome.details.Add("失败 " + path + "：" + e.Message);
                    continue;
                }

                if (!changed) { outcome.skippedCount++; continue; }

                outcome.changedCount++;
                if (before.Count > 0) undo.Add(new UndoItem(path, before));
                if (outcome.details.Count < 10) outcome.details.Add(path);
            }

            outcome.success = outcome.changedCount > 0;
            outcome.needsReanalyze = outcome.changedCount > 0;
            outcome.message = outcome.changedCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "已修改 {0} 项（跳过 {1} 项）", outcome.changedCount, outcome.skippedCount)
                : "所有目标都已符合预期，无需修改。";
            return outcome;
        }

        static PerfFixOutcome ApplyProjectSetting(PerfFixStep step, List<UndoItem> undo, Func<Dictionary<string, string>, bool> mutate)
        {
            var outcome = new PerfFixOutcome();
            var before = new Dictionary<string, string>();

            if (mutate(before))
            {
                outcome.changedCount = 1;
                outcome.success = true;
                outcome.needsReanalyze = true;
                outcome.message = "已更新 " + step.title + "（可在变更日志里撤销）";
                if (before.Count > 0) undo.Add(new UndoItem("ProjectSettings", before));
            }
            else
            {
                outcome.message = "当前设置已是目标值，无需修改。";
            }
            return outcome;
        }

        delegate bool SceneMutator(GameObject go, Dictionary<string, string> before);

        static PerfFixOutcome ApplySceneObjects(PerfFixStep step, List<UndoItem> undo, SceneMutator mutate)
        {
            var outcome = new PerfFixOutcome();
            int total = step.targets.Count;

            for (int i = 0; i < total; i++)
            {
                string path = step.targets[i];
                if (Progress != null) Progress(i, total, path);

                var go = FindByPath(path);
                if (go == null) { outcome.skippedCount++; outcome.details.Add("未找到对象：" + path); continue; }

                var before = new Dictionary<string, string>();
                bool changed;
                try { changed = mutate(go, before); }
                catch (Exception e)
                {
                    outcome.skippedCount++;
                    outcome.details.Add("失败 " + path + "：" + e.Message);
                    continue;
                }

                if (!changed) { outcome.skippedCount++; continue; }

                outcome.changedCount++;
                if (before.Count > 0) undo.Add(new UndoItem(path, before));
                if (outcome.details.Count < 10) outcome.details.Add(path);
            }

            outcome.success = outcome.changedCount > 0;
            outcome.needsReanalyze = outcome.changedCount > 0;
            outcome.message = outcome.changedCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "已修改 {0} 个场景对象（跳过 {1} 个）", outcome.changedCount, outcome.skippedCount)
                : "没有需要修改的场景对象。";
            return outcome;
        }

        // =====================================================================
        // 纹理
        // =====================================================================

        static bool MutateTextureReadWrite(string path, Dictionary<string, string> before)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null || !ti.isReadable) return false;
            before["isReadable"] = ti.isReadable ? "1" : "0";
            ti.isReadable = false;
            ti.SaveAndReimport();
            return true;
        }

        static bool MutateTextureCompression(string path, Dictionary<string, string> before)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null || ti.textureCompression != TextureImporterCompression.Uncompressed) return false;
            before["textureCompression"] = ti.textureCompression.ToString();
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.SaveAndReimport();
            return true;
        }

        static bool MutateTextureMaxSize(string path, Dictionary<string, string> before)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null || ti.maxTextureSize <= 2048) return false;
            before["maxTextureSize"] = ti.maxTextureSize.ToString(CultureInfo.InvariantCulture);
            ti.maxTextureSize = 2048;
            ti.SaveAndReimport();
            return true;
        }

        static bool MutateTextureStreaming(string path, Dictionary<string, string> before)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null || ti.streamingMipmaps) return false;
            before["streamingMipmaps"] = ti.streamingMipmaps ? "1" : "0";
            ti.streamingMipmaps = true;
            ti.SaveAndReimport();
            return true;
        }

        // =====================================================================
        // 模型
        // =====================================================================

        static bool MutateModelReadWrite(string path, Dictionary<string, string> before)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null || !mi.isReadable) return false;
            before["isReadable"] = mi.isReadable ? "1" : "0";
            mi.isReadable = false;
            mi.SaveAndReimport();
            return true;
        }

        static bool MutateModelCollider(string path, Dictionary<string, string> before)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null || !mi.addCollider) return false;
            before["addCollider"] = mi.addCollider ? "1" : "0";
            mi.addCollider = false;
            mi.SaveAndReimport();
            return true;
        }

        static bool MutateModelBlendShapes(string path, Dictionary<string, string> before)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null || !mi.importBlendShapes) return false;
            before["importBlendShapes"] = mi.importBlendShapes ? "1" : "0";
            mi.importBlendShapes = false;
            mi.SaveAndReimport();
            return true;
        }

        static bool MutateModelMeshCompression(string path, Dictionary<string, string> before)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return false;
            if (mi.meshCompression != ModelImporterMeshCompression.Off) return false;
            before["meshCompression"] = mi.meshCompression.ToString();
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.SaveAndReimport();
            return true;
        }

        // =====================================================================
        // 音频
        // =====================================================================

        static bool MutateAudioLoadType(string path, Dictionary<string, string> before)
        {
            var ai = AssetImporter.GetAtPath(path) as AudioImporter;
            if (ai == null) return false;

            var settings = ai.defaultSampleSettings;
            if (settings.loadType != AudioClipLoadType.DecompressOnLoad) return false;

            before["loadType"] = settings.loadType.ToString();
            long size = FileSize(path);
            settings.loadType = size > 4 * 1024 * 1024
                ? AudioClipLoadType.Streaming
                : AudioClipLoadType.CompressedInMemory;

            ai.defaultSampleSettings = settings;
            ai.SaveAndReimport();
            return true;
        }

        /// <summary>
        /// preloadAudioData 在 Unity 2022+ 移到了 defaultSampleSettings 内部（旧属性已 Obsolete-Error），
        /// 且它是 struct，所以必须装箱改字段再整体写回。
        /// </summary>
        static bool MutateAudioPreload(string path, Dictionary<string, string> before)
        {
            var ai = AssetImporter.GetAtPath(path) as AudioImporter;
            if (ai == null) return false;

            object boxed = ai.defaultSampleSettings;
            var field = boxed.GetType().GetField("preloadAudioData", BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(bool)) return false;
            if (!(bool)field.GetValue(boxed)) return false;

            before["preloadAudioData"] = "1";
            field.SetValue(boxed, false);
            ai.defaultSampleSettings = (AudioImporterSampleSettings)boxed;
            ai.SaveAndReimport();
            return true;
        }

        // =====================================================================
        // 场景对象
        // =====================================================================

        static bool MutateSkinnedOffscreen(GameObject go, Dictionary<string, string> before)
        {
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null || !smr.updateWhenOffscreen) return false;

            before["updateWhenOffscreen"] = "1";
            Undo.RecordObject(smr, "PerfAgent: 关闭 updateWhenOffscreen");
            smr.updateWhenOffscreen = false;
            EditorUtility.SetDirty(smr);
            return true;
        }

        static bool MutateMarkStatic(GameObject go, Dictionary<string, string> before)
        {
            if (go.isStatic) return false;

            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            before["staticFlags"] = ((int)flags).ToString(CultureInfo.InvariantCulture);

            Undo.RecordObject(go, "PerfAgent: 标记 Static");
            GameObjectUtility.SetStaticEditorFlags(go, flags | StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
            EditorUtility.SetDirty(go);
            return true;
        }

        // =====================================================================
        // 撤销实现
        // =====================================================================

        static void UndoTexture(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                var ti = AssetImporter.GetAtPath(item.target) as TextureImporter;
                if (ti == null) continue;

                string v;
                if (item.values.TryGetValue("isReadable", out v)) ti.isReadable = v == "1";
                if (item.values.TryGetValue("streamingMipmaps", out v)) ti.streamingMipmaps = v == "1";
                if (item.values.TryGetValue("textureCompression", out v)) ti.textureCompression = ParseEnum(v, ti.textureCompression);
                if (item.values.TryGetValue("maxTextureSize", out v))
                {
                    int n;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ti.maxTextureSize = n;
                }

                ti.SaveAndReimport();
                outcome.changedCount++;
            }
        }

        static void UndoModel(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                var mi = AssetImporter.GetAtPath(item.target) as ModelImporter;
                if (mi == null) continue;

                string v;
                if (item.values.TryGetValue("isReadable", out v)) mi.isReadable = v == "1";
                if (item.values.TryGetValue("addCollider", out v)) mi.addCollider = v == "1";
                if (item.values.TryGetValue("importBlendShapes", out v)) mi.importBlendShapes = v == "1";
                if (item.values.TryGetValue("meshCompression", out v)) mi.meshCompression = ParseEnum(v, mi.meshCompression);

                mi.SaveAndReimport();
                outcome.changedCount++;
            }
        }

        static void UndoAudio(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                var ai = AssetImporter.GetAtPath(item.target) as AudioImporter;
                if (ai == null) continue;

                var settings = ai.defaultSampleSettings;
                string v;
                if (item.values.TryGetValue("loadType", out v)) settings.loadType = ParseEnum(v, settings.loadType);
                ai.defaultSampleSettings = settings;

                if (item.values.TryGetValue("preloadAudioData", out v) && v == "1")
                {
                    object boxed = ai.defaultSampleSettings;
                    var field = boxed.GetType().GetField("preloadAudioData", BindingFlags.Public | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        field.SetValue(boxed, true);
                        ai.defaultSampleSettings = (AudioImporterSampleSettings)boxed;
                    }
                }

                ai.SaveAndReimport();
                outcome.changedCount++;
            }
        }

        static void UndoProjectSetting(List<UndoItem> items, PerfFixOutcome outcome)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string v;

                if (item.values.TryGetValue("autoSyncTransforms", out v))
                {
                    Physics.autoSyncTransforms = v == "1";
                    outcome.changedCount++;
                }

                if (item.values.TryGetValue("fixedDeltaTime", out v))
                {
                    float f;
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    {
                        Time.fixedDeltaTime = f;
                        outcome.changedCount++;
                    }
                }
            }

            if (outcome.changedCount > 0) AssetDatabase.SaveAssets();
        }

        static void UndoScene(List<UndoItem> items, PerfFixOutcome outcome, string key)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (Progress != null) Progress(i, items.Count, item.target);

                var go = FindByPath(item.target);
                if (go == null) continue;

                string v;
                if (key == "updateWhenOffscreen" && item.values.TryGetValue(key, out v))
                {
                    var smr = go.GetComponent<SkinnedMeshRenderer>();
                    if (smr == null) continue;
                    Undo.RecordObject(smr, "PerfAgent: 撤销 updateWhenOffscreen");
                    smr.updateWhenOffscreen = v == "1";
                    EditorUtility.SetDirty(smr);
                    outcome.changedCount++;
                }
                else if (key == "staticFlags" && item.values.TryGetValue(key, out v))
                {
                    int flags;
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out flags)) continue;
                    Undo.RecordObject(go, "PerfAgent: 撤销静态标记");
                    GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)flags);
                    EditorUtility.SetDirty(go);
                    outcome.changedCount++;
                }
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            try
            {
                if (Enum.IsDefined(typeof(T), value)) return (T)Enum.Parse(typeof(T), value, false);
            }
            catch { }
            return fallback;
        }

        static long FileSize(string assetPath)
        {
            try
            {
                var info = new FileInfo(assetPath);
                return info.Exists ? info.Length : 0;
            }
            catch { return 0; }
        }

        /// <summary>按 "Root/Child/Leaf" 层级路径在当前场景里找对象（与审计采集器写入的路径格式一致）。</summary>
        static GameObject FindByPath(string hierarchyPath)
        {
            if (string.IsNullOrEmpty(hierarchyPath)) return null;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;

            string[] segments = hierarchyPath.Split('/');
            var roots = scene.GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] == null || roots[i].name != segments[0]) continue;
                if (segments.Length == 1) return roots[i];

                var sb = new StringBuilder();
                for (int s = 1; s < segments.Length; s++)
                {
                    if (s > 1) sb.Append('/');
                    sb.Append(segments[s]);
                }

                var child = roots[i].transform.Find(sb.ToString());
                return child == null ? null : child.gameObject;
            }

            return null;
        }

        // =====================================================================
        // 撤销载荷（执行器私有格式，日志层不需要理解）
        // =====================================================================

        class UndoItem
        {
            public string target = "";
            public Dictionary<string, string> values = new Dictionary<string, string>();

            public UndoItem() { }

            public UndoItem(string target, Dictionary<string, string> values)
            {
                this.target = target;
                this.values = values;
            }

            public static string Build(List<UndoItem> items)
            {
                if (items == null || items.Count == 0) return "";

                var list = new List<object>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = new Dictionary<string, object>();
                    entry["target"] = items[i].target;
                    var v = new Dictionary<string, object>();
                    foreach (var kv in items[i].values) v[kv.Key] = kv.Value;
                    entry["values"] = v;
                    list.Add(entry);
                }
                return MiniJson.Serialize(list);
            }

            public static List<UndoItem> Parse(string payload)
            {
                var result = new List<UndoItem>();
                if (string.IsNullOrEmpty(payload)) return result;

                try
                {
                    var list = MiniJson.ParseSafe(payload) as List<object>;
                    if (list == null) return result;

                    for (int i = 0; i < list.Count; i++)
                    {
                        var entry = MiniJson.AsDict(list[i]);
                        if (entry == null) continue;

                        var item = new UndoItem();
                        item.target = MiniJson.Str(entry, "target", "");
                        var values = MiniJson.AsDict(MiniJson.Get(entry, "values"));
                        if (values != null)
                        {
                            foreach (var kv in values)
                                item.values[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                        }
                        result.Add(item);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PerfAgent] 还原信息解析失败: " + e.Message);
                }
                return result;
            }
        }
    }
}

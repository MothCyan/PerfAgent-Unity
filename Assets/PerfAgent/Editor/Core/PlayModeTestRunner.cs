using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PerfAgent.Collectors;
using PerfAgent.Utils;

namespace PerfAgent.Core
{
    /// <summary>
    /// 自动「走一遍游戏循环并采集性能」的驱动器。
    ///
    /// 这不是简单地调一下 PerfPipeline.CaptureFrames —— 整个流程要穿过**两次域重载**
    /// （进入 Play、退出 Play 各一次），而域重载会把所有静态字段清空。如果不处理，
    /// 流程会在刚进 Play 或刚退出 Play 时静默断掉，表现为「面板停在采样中不动了」。
    ///
    /// 因此状态机的设计是：
    ///  1. 任务状态全部落在磁盘（ProjectSettings/PerfAgent/PlayModeRuns），
    ///     静态字段只做缓存，跨域重载靠 SessionState 记住「当前任务是哪个」；
    ///  2. [InitializeOnLoadMethod] 在每个新域里恢复未完成的任务并重新挂 update；
    ///  3. **快照必须在退出 Play 模式之前落盘** —— 那之后我们只读文件，不再依赖任何静态状态；
    ///  4. 有兜底超时，任何一步卡住都会强制退出 Play，不让编辑器停在 Play 模式里出不来。
    ///
    /// 采样口径与手动抓帧完全一致（同一个 FrameCapture / PerfPipeline），
    /// 所以自动跑出来的数字和面板里点「抓帧」得到的是同一套，不会出现两套标准。
    /// </summary>
    public static class PlayModeTestRunner
    {
        const string SessionJobId = "PerfAgent.PlayModeTest.JobId";
        const double ProgressWriteInterval = 0.5;

        static PlayModeTestJob _job;
        static FrameCapture _capture;
        static int _warmupStartFrame;
        static double _deadline;
        static double _nextProgressWrite;
        static bool _ticking;

        // =====================================================================
        // 存储
        // =====================================================================

        public static string RootDir
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "PerfAgent"), "PlayModeRuns");
            }
        }

        public static string CurrentPath { get { return Path.Combine(RootDir, "current.json"); } }

        static void EnsureDir()
        {
            if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
        }

        static void Save(PlayModeTestJob job)
        {
            if (job == null) return;
            try
            {
                EnsureDir();
                string json = job.ToJson();
                File.WriteAllText(Path.Combine(RootDir, job.id + ".json"), json);
                File.WriteAllText(CurrentPath, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PerfAgent] 写入自动测试任务状态失败: " + e.Message);
            }
        }

        static PlayModeTestJob LoadFromDisk(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return PlayModeTestJob.Parse(File.ReadAllText(path));
            }
            catch { return null; }
        }

        /// <summary>当前任务：优先内存里的活任务，否则回退到磁盘上的 current.json。</summary>
        public static PlayModeTestJob LoadCurrent()
        {
            if (_job != null && _job.IsActive()) return _job;
            return LoadFromDisk(CurrentPath);
        }

        public static PlayModeTestJob LoadById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_job != null && _job.id == id) return _job;
            return LoadFromDisk(Path.Combine(RootDir, id + ".json"));
        }

        public static List<string> ListRuns()
        {
            var result = new List<string>();
            if (!Directory.Exists(RootDir)) return result;

            var files = Directory.GetFiles(RootDir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                if (name != "current") result.Add(name);
            }
            return result;
        }

        // =====================================================================
        // 启动 / 取消
        // =====================================================================

        /// <summary>启动一次自动测试。立即返回（真正的工作在编辑器主循环里推进）。</summary>
        public static PlayModeTestJob Start(PlayModeTestJob request, out string error)
        {
            error = null;
            if (request == null) { error = "缺少任务参数。"; return null; }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                error = "编辑器正在编译或导入资源，等它结束再启动自动测试。";
                return null;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                error = "编辑器已在 Play 模式。先退出 Play，再启动自动测试。";
                return null;
            }

            var active = LoadCurrent();
            if (active != null && active.IsActive())
            {
                error = "已有自动测试在进行（" + active.id + "：" + active.PhaseText() + "）。"
                      + "用 perf_playmode_test_status 查看，或先用 perf_playmode_test_cancel 取消。";
                return null;
            }
            if (PerfSession.Capturing)
            {
                error = "已有抓帧任务在进行中，等它结束再启动自动测试。";
                return null;
            }

            request.warmupFrames = Clamp(request.warmupFrames, 0, 100000);
            request.captureFrames = Clamp(request.captureFrames, 10, PerfPipeline.MaxCaptureFrames);

            if (request.durationSeconds < 0) request.durationSeconds = 0;
            if (request.durationSeconds > PerfPipeline.MaxCaptureSeconds)
                request.durationSeconds = PerfPipeline.MaxCaptureSeconds;

            // 兜底超时必须能盖住采集窗口，否则会被自己的超时保护打断。
            double minimumTimeout = request.durationSeconds > 0
                ? request.durationSeconds + request.warmupFrames / 30.0 + 120
                : 30;
            if (request.timeoutSeconds < minimumTimeout) request.timeoutSeconds = minimumTimeout;

            // 指定了场景就先切过去。切场景可能丢掉未保存的修改，所以让用户确认。
            if (!string.IsNullOrEmpty(request.scenePath))
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string full = Path.Combine(projectRoot, request.scenePath);
                if (!File.Exists(full))
                {
                    error = "场景不存在：" + request.scenePath;
                    return null;
                }

                if (SceneManager.GetActiveScene().path != request.scenePath)
                {
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    {
                        error = "已取消：切换场景需要先处理未保存的修改。";
                        return null;
                    }
                    EditorSceneManager.OpenScene(request.scenePath, OpenSceneMode.Single);
                }
            }

            request.id = "PM" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            request.phase = PlayModeTestPhase.Entering;
            request.startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            request.finishedUtc = "";
            request.previousScenePath = SceneManager.GetActiveScene().path;
            request.capturedFrames = 0;
            request.error = "";
            request.AddEvent("任务创建，准备进入 Play 模式");

            _job = request;
            Save(request);
            SessionState.SetString(SessionJobId, request.id);

            _deadline = EditorApplication.timeSinceStartup + request.timeoutSeconds + 30;
            _nextProgressWrite = 0;
            Hook();

            // 用 delayCall 而不是直接调用：EnterPlaymode 必须在编辑器空闲时下发
            EditorApplication.delayCall += delegate
            {
                if (_job == null || _job.phase != PlayModeTestPhase.Entering) return;

                // 趁还在编辑模式，量一次编辑器开销基线 —— 进了 Play 就测不到了，
                // 而没基线就无法把编辑器自身的分配从「每帧托管分配」里区分出来。
                EditorOverheadBaseline.Measure(delegate
                {
                    EditorApplication.delayCall += delegate
                    {
                        if (_job == null || _job.phase != PlayModeTestPhase.Entering) return;
                        EditorApplication.EnterPlaymode();
                    };
                });
            };

            return request;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static bool Cancel(string reason, out string error)
        {
            error = null;
            var job = LoadCurrent();
            if (job == null || !job.IsActive())
            {
                error = "当前没有进行中的自动测试。";
                return false;
            }

            _job = job;
            AbortJob(job, PlayModeTestPhase.Cancelled, string.IsNullOrEmpty(reason) ? "已取消。" : reason);
            return true;
        }

        // =====================================================================
        // 状态机
        // =====================================================================

        static void Hook()
        {
            if (_ticking) return;
            _ticking = true;
            EditorApplication.update += Tick;
        }

        static void Unhook()
        {
            if (!_ticking) return;
            _ticking = false;
            EditorApplication.update -= Tick;
        }

        /// <summary>
        /// 每个新域都会跑一次。若 SessionState 里还留着一个没结束的任务，
        /// 说明上一个域是在流程中途被域重载打断的 —— 在这里接着走下去。
        /// </summary>
        [InitializeOnLoadMethod]
        static void Bootstrap()
        {
            string id = SessionState.GetString(SessionJobId, "");
            if (string.IsNullOrEmpty(id)) return;

            var job = LoadFromDisk(Path.Combine(RootDir, id + ".json"));
            if (job == null || !job.IsActive())
            {
                SessionState.EraseString(SessionJobId);
                return;
            }

            _job = job;
            _deadline = EditorApplication.timeSinceStartup + job.timeoutSeconds + 30;
            job.AddEvent("域重载后恢复（" + job.PhaseText() + "）");
            Save(job);
            Hook();
        }

        static void Tick()
        {
            if (_job == null) { Unhook(); return; }

            if (EditorApplication.timeSinceStartup > _deadline)
            {
                AbortJob(_job, PlayModeTestPhase.Failed,
                    "整体超时（上限 " + _job.timeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + "s）："
                    + "流程没能在预期时间内走完，已强制退出 Play 模式。");
                return;
            }

            switch (_job.phase)
            {
                case PlayModeTestPhase.Entering:
                    // 域重载已经发生过，这里等 Play 模式真正跑起来
                    if (!EditorApplication.isPlaying) return;
                    _job.phase = PlayModeTestPhase.Warming;
                    _warmupStartFrame = Time.frameCount;
                    _job.AddEvent("已进入 Play 模式，先丢弃 " + _job.warmupFrames + " 帧启动抖动");
                    Save(_job);
                    RunSetupMethod(_job);
                    break;

                case PlayModeTestPhase.Warming:
                    if (Time.frameCount - _warmupStartFrame < _job.warmupFrames) return;
                    _job.phase = PlayModeTestPhase.Capturing;
                    _job.AddEvent("预热结束，开始连续采样 " + _job.captureFrames + " 帧");
                    Save(_job);
                    StartCapture(_job);
                    break;

                case PlayModeTestPhase.Capturing:
                    // 用户手动退出 Play 会让帧计数冻结，这时不该干等到超时
                    if (!EditorApplication.isPlaying)
                    {
                        AbortJob(_job, PlayModeTestPhase.Failed, "Play 模式被手动退出，采样中断。");
                        return;
                    }
                    ReportProgress(_job);
                    break;

                case PlayModeTestPhase.Finalizing:
                    if (EditorApplication.isPlaying) return;   // 还在退出过程中
                    Finish(_job);
                    break;
            }
        }

        static void StartCapture(PlayModeTestJob job)
        {
            PerfSession.Capturing = true;
            job.captureStartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            Action<List<FrameStat>> done = delegate (List<FrameStat> frames) { OnCaptureDone(job, frames); };

            _capture = job.durationSeconds > 0
                ? new FrameCapture(job.captureFrames, job.durationSeconds, done, null)
                : new FrameCapture(job.captureFrames, done, null);

            _capture.Start();
        }

        static void OnCaptureDone(PlayModeTestJob job, List<FrameStat> frames)
        {
            _capture = null;
            PerfSession.Capturing = false;

            if (job == null || job.IsTerminal()) return;

            try
            {
                var snap = PerfPipeline.CreateSnapshot(
                    string.IsNullOrEmpty(job.label) ? ("Play 模式自动测试 " + job.id) : job.label);
                snap.scenePath = SceneManager.GetActiveScene().path;

                FrameCapture.Summarize(snap, frames);
                snap.AddNote(job.durationSeconds > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "数据来自自动 Play 模式测试：先丢弃 {0} 帧启动抖动，再连续采集 {1:0.#} 秒（{2} 帧，"
                        + "实际帧率 {3:0.#} FPS）。时长模式下帧数由帧率决定，因此这份数据覆盖了流程内的全部阶段，"
                        + "不只是稳态 —— 看结论时请把阶段性尖峰一并纳入判断。",
                        job.warmupFrames, job.durationSeconds, frames.Count,
                        frames.Count / Math.Max(0.001, job.durationSeconds))
                    : string.Format(CultureInfo.InvariantCulture,
                        "数据来自自动 Play 模式测试：先丢弃 {0} 帧启动抖动，再连续采样 {1} 帧。"
                        + "采样期间的平均帧耗时/GC 分配代表游戏循环稳定后的表现，不含场景加载与 JIT 的开销。",
                        job.warmupFrames, frames.Count));

                PerfPipeline.RunCollectors(snap, false, null);
                PerfPipeline.Analyze(snap);

                string path = PerfPipeline.SaveAndSetCurrent(snap);

                job.snapshotId = snap.id;
                job.snapshotPath = path;
                job.capturedFrames = frames.Count;
                job.phase = PlayModeTestPhase.Finalizing;
                job.AddEvent("快照已落盘：" + snap.id + "（" + frames.Count + " 帧）");
                Save(job);
            }
            catch (Exception e)
            {
                AbortJob(job, PlayModeTestPhase.Failed, "采样后处理失败：" + e.Message);
                return;
            }

            // 快照已经在磁盘上了，这时退出 Play 触发域重载是安全的 ——
            // 新域里我们只读文件，不再依赖任何静态字段。
            EditorApplication.delayCall += delegate
            {
                if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            };
        }

        static void Finish(PlayModeTestJob job)
        {
            if (job == null) { Unhook(); return; }

            job.phase = PlayModeTestPhase.Succeeded;
            job.finishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            job.AddEvent("流程结束（耗时 " + job.ElapsedSeconds().ToString("0.0", CultureInfo.InvariantCulture) + "s）");
            Save(job);

            RestoreScene(job);

            SessionState.EraseString(SessionJobId);
            Unhook();
            _job = null;
        }

        static void AbortJob(PlayModeTestJob job, PlayModeTestPhase phase, string reason)
        {
            if (job != null && !job.IsTerminal())
            {
                job.phase = phase;
                job.error = reason;
                job.finishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                job.AddEvent("中止：" + reason);
                Save(job);
            }

            if (_capture != null)
            {
                try { _capture.Cancel(); } catch { }
                _capture = null;
            }
            PerfSession.Capturing = false;

            SessionState.EraseString(SessionJobId);
            Unhook();
            _job = null;

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += delegate
                {
                    if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                };
            }
        }

        static void RestoreScene(PlayModeTestJob job)
        {
            if (!job.restoreScene) return;
            string previous = job.previousScenePath;
            if (string.IsNullOrEmpty(previous)) return;
            if (SceneManager.GetActiveScene().path == previous) return;

            EditorApplication.delayCall += delegate
            {
                try
                {
                    if (SceneManager.GetActiveScene().path == previous) return;
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                    EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
                }
                catch (Exception e)
                {
                    job.AddEvent("恢复场景失败：" + e.Message);
                    Save(job);
                }
            };
        }

        static void ReportProgress(PlayModeTestJob job)
        {
            if (_capture == null) return;

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextProgressWrite) return;
            _nextProgressWrite = now + ProgressWriteInterval;

            int done = _capture.CapturedCount;
            if (done == job.capturedFrames) return;

            job.capturedFrames = done;
            Save(job);
        }

        // =====================================================================
        // 准备方法
        // =====================================================================

        static void RunSetupMethod(PlayModeTestJob job)
        {
            if (string.IsNullOrEmpty(job.setupMethod)) return;

            string typeName, methodName;
            if (!PlayModeTestJob.SplitSetupMethod(job.setupMethod, out typeName, out methodName))
            {
                job.AddEvent("准备方法格式无效（应为 命名空间.类型.方法）：" + job.setupMethod);
                Save(job);
                return;
            }

            var type = Reflect.FindType(typeName);
            if (type == null)
            {
                job.AddEvent("准备方法所在类型未找到：" + typeName);
                Save(job);
                return;
            }

            try
            {
                bool ok = Reflect.InvokeStaticVoid(type, methodName);
                job.AddEvent(ok
                    ? ("已调用准备方法 " + typeName + "." + methodName)
                    : ("准备方法调用失败（可能不是无参静态方法）：" + job.setupMethod));
            }
            catch (Exception e)
            {
                job.AddEvent("准备方法抛异常：" + e.Message);
            }

            Save(job);
        }
    }
}

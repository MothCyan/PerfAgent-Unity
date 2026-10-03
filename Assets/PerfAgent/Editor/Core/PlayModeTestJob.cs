using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Utils;

namespace PerfAgent.Core
{
    /// <summary>自动 Play 模式性能测试的阶段。</summary>
    public enum PlayModeTestPhase
    {
        None,
        /// <summary>已下 EnterPlaymode，等域重载 + 真正进入 Play。</summary>
        Entering,
        /// <summary>已在 Play 模式，正在丢掉启动抖动帧。</summary>
        Warming,
        /// <summary>正在逐帧采样。</summary>
        Capturing,
        /// <summary>采样完成且快照已落盘，等退出 Play 模式。</summary>
        Finalizing,
        Succeeded,
        Failed,
        Cancelled,
    }

    /// <summary>
    /// 一次「自动走游戏循环并采集性能」的任务。
    ///
    /// 为什么要做成可序列化的数据对象：进入/退出 Play 模式各会触发一次域重载，
    /// 所有静态字段都会被清空。任务状态必须能落盘、并在新域里原样恢复，
    /// 否则流程会在域重载处断掉。
    ///
    /// 本类刻意不引用 UnityEngine —— 序列化与状态判定不依赖编辑器，
    /// 因此能被 Tests~ 下的独立回归工程直接编译测试。
    /// </summary>
    public class PlayModeTestJob
    {
        public string id;
        public string label;

        /// <summary>测试前要打开的场景；为空表示用当前打开的场景。</summary>
        public string scenePath;

        /// <summary>预热帧数：进入 Play 后的前若干帧包含 JIT、资源加载、场景初始化，必须丢掉。</summary>
        public int warmupFrames = 60;

        /// <summary>真正参与统计的采样帧数（时长模式下退化为安全上限）。</summary>
        public int captureFrames = 300;

        /// <summary>
        /// >0 表示按**时长**采集：跑够这么多秒自动停，captureFrames 退化为安全上限。
        ///
        /// 「走完一整个游戏流程」是按秒描述的 —— 用帧数说既不准（帧率变了就不是同一段时间）
        /// 也难换算（用户得自己乘）。
        /// </summary>
        public double durationSeconds;

        /// <summary>整条流程的兜底超时，防止编辑器卡在 Play 模式里出不来。</summary>
        public double timeoutSeconds = 240;

        /// <summary>可选：进入 Play 后调用的静态方法（"命名空间.类型.方法"），用于把游戏摆到要测的状态。</summary>
        public string setupMethod;

        /// <summary>测试结束后是否恢复测试前的场景。</summary>
        public bool restoreScene = true;

        public PlayModeTestPhase phase = PlayModeTestPhase.None;
        public int capturedFrames;

        /// <summary>采样阶段开始的 UTC 时间（时长模式算进度要用）。</summary>
        public string captureStartedUtc;

        public string startedUtc;
        public string finishedUtc;
        public string snapshotId;
        public string snapshotPath;

        /// <summary>测试开始前打开的场景，用于结束后的恢复。</summary>
        public string previousScenePath;

        public string error;

        readonly List<string> _events = new List<string>();

        /// <summary>阶段流水，供调用方判断卡在哪一步。只保留最近若干条，避免文件无限膨胀。</summary>
        public IList<string> Events { get { return _events; } }

        public bool IsTerminal()
        {
            return phase == PlayModeTestPhase.Succeeded
                || phase == PlayModeTestPhase.Failed
                || phase == PlayModeTestPhase.Cancelled;
        }

        public bool IsActive()
        {
            return phase == PlayModeTestPhase.Entering
                || phase == PlayModeTestPhase.Warming
                || phase == PlayModeTestPhase.Capturing
                || phase == PlayModeTestPhase.Finalizing;
        }

        public void AddEvent(string message)
        {
            _events.Add(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
            while (_events.Count > 40) _events.RemoveAt(0);
        }

        public string PhaseText()
        {
            switch (phase)
            {
                case PlayModeTestPhase.Entering: return "正在进入 Play 模式";
                case PlayModeTestPhase.Warming: return "预热中（丢弃启动抖动帧）";
                case PlayModeTestPhase.Capturing: return "采样中";
                case PlayModeTestPhase.Finalizing: return "采样完成，正在退出 Play 模式";
                case PlayModeTestPhase.Succeeded: return "已完成";
                case PlayModeTestPhase.Failed: return "失败";
                case PlayModeTestPhase.Cancelled: return "已取消";
                default: return "未开始";
            }
        }

        /// <summary>0~1 的整体进度；时长模式下按已采时长换算。</summary>
        public double Progress()
        {
            int warm = Math.Max(0, warmupFrames);
            int cap = Math.Max(1, captureFrames);

            switch (phase)
            {
                case PlayModeTestPhase.Warming:
                    return durationSeconds > 0 ? 0.05 : 0.0;

                case PlayModeTestPhase.Capturing:
                    if (durationSeconds > 0)
                    {
                        // 时长模式下不知道总共会采多少帧，只能用耗时算。
                        // 预热段长度不确定，所以把采样阶段映射到 0.1~0.99 这一段。
                        double ratio = Math.Min(1.0, SecondsSinceCaptureStart() / durationSeconds);
                        return Math.Min(0.99, 0.1 + ratio * 0.89);
                    }
                    return Math.Min(0.99, (double)(warm + capturedFrames) / (warm + cap));

                case PlayModeTestPhase.Finalizing:
                case PlayModeTestPhase.Succeeded:
                    return 1.0;

                default:
                    return 0.0;
            }
        }

        double SecondsSinceCaptureStart()
        {
            DateTime started;
            if (!DateTime.TryParse(captureStartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started))
                return 0;

            return Math.Max(0, (DateTime.UtcNow - started).TotalSeconds);
        }

        public double ElapsedSeconds()
        {
            DateTime started;
            if (!DateTime.TryParse(startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started))
                return 0;

            DateTime end;
            if (!DateTime.TryParse(finishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end))
                end = DateTime.UtcNow;

            return Math.Max(0, (end - started).TotalSeconds);
        }

        // =====================================================================
        // 序列化：用 MiniJson 手写映射，字段名与对外 JSON 保持一致（下划线命名）
        // =====================================================================

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["id"] = id ?? "";
            d["label"] = label ?? "";
            d["scene_path"] = scenePath ?? "";
            d["warmup_frames"] = warmupFrames;
            d["capture_frames"] = captureFrames;
            d["duration_seconds"] = durationSeconds;
            d["timeout_seconds"] = timeoutSeconds;
            d["setup_method"] = setupMethod ?? "";
            d["restore_scene"] = restoreScene;
            d["phase"] = phase.ToString();
            d["phase_text"] = PhaseText();
            d["captured_frames"] = capturedFrames;
            d["capture_started_utc"] = captureStartedUtc ?? "";
            d["progress"] = Math.Round(Progress(), 3);
            d["started_utc"] = startedUtc ?? "";
            d["finished_utc"] = finishedUtc ?? "";
            d["snapshot_id"] = snapshotId ?? "";
            d["snapshot_path"] = snapshotPath ?? "";
            d["previous_scene_path"] = previousScenePath ?? "";
            d["error"] = error ?? "";

            var events = new List<object>(_events.Count);
            for (int i = 0; i < _events.Count; i++) events.Add(_events[i]);
            d["events"] = events;
            return d;
        }

        public static PlayModeTestJob FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null;

            var job = new PlayModeTestJob();
            job.id = Str(d, "id", "");
            job.label = Str(d, "label", "");
            job.scenePath = Str(d, "scene_path", "");
            job.warmupFrames = Int(d, "warmup_frames", 60);
            job.captureFrames = Int(d, "capture_frames", 300);
            job.durationSeconds = Dbl(d, "duration_seconds", 0);
            job.timeoutSeconds = Dbl(d, "timeout_seconds", 240);
            job.setupMethod = Str(d, "setup_method", "");
            job.restoreScene = Bool(d, "restore_scene", true);
            job.phase = ParsePhase(Str(d, "phase", ""));
            job.capturedFrames = Int(d, "captured_frames", 0);
            job.captureStartedUtc = Str(d, "capture_started_utc", "");
            job.startedUtc = Str(d, "started_utc", "");
            job.finishedUtc = Str(d, "finished_utc", "");
            job.snapshotId = Str(d, "snapshot_id", "");
            job.snapshotPath = Str(d, "snapshot_path", "");
            job.previousScenePath = Str(d, "previous_scene_path", "");
            job.error = Str(d, "error", "");

            object rawEvents;
            var list = d.TryGetValue("events", out rawEvents) ? rawEvents as List<object> : null;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) job._events.Add(Convert.ToString(list[i], CultureInfo.InvariantCulture));

            return job;
        }

        public string ToJson()
        {
            return MiniJson.Serialize(ToDict());
        }

        public static PlayModeTestJob Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return FromDict(MiniJson.ParseObjectSafe(json));
        }

        static PlayModeTestPhase ParsePhase(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return PlayModeTestPhase.None;
            try { return (PlayModeTestPhase)Enum.Parse(typeof(PlayModeTestPhase), raw, true); }
            catch { return PlayModeTestPhase.None; }
        }

        // =====================================================================
        // 准备方法的解析
        // =====================================================================

        /// <summary>
        /// 拆 "命名空间.类型.方法"；允许写法带括号（Foo.Bar()）。
        /// 非法输入一律返回 false，而不是拼出一个奇怪的类型名再去反射。
        /// 放在这里是为了让它不依赖 Unity、能被独立回归工程覆盖。
        /// </summary>
        public static bool SplitSetupMethod(string spec, out string typeName, out string methodName)
        {
            typeName = null;
            methodName = null;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            string s = spec.Trim();
            int paren = s.IndexOf('(');
            if (paren > 0) s = s.Substring(0, paren).Trim();
            s = s.TrimEnd('.');

            int dot = s.LastIndexOf('.');
            if (dot <= 0 || dot == s.Length - 1) return false;

            typeName = s.Substring(0, dot);
            methodName = s.Substring(dot + 1);
            return true;
        }

        static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s) ? def : s;
        }

        static int Int(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;

            int parsed;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : def;
        }

        static double Dbl(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;

            double parsed;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : def;
        }

        static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return def;
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }
    }
}

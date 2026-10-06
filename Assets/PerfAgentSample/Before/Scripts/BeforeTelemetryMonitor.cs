using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 【优化之前 · 反面样板】
///
/// 一个「每帧自检 / 遥测上报」脚本的新手写法：功能上没问题（只是统计游戏内的刚体速度、
/// 存活数量与相机位置），但把常见的每帧开销全踩了一遍。它是为 PerfAgent 准备的故障样本，
/// 不是推荐写法 —— 同一份功能的正确实现见同工程 After/Scripts/AfterTelemetryMonitor.cs。
///
/// 之所以能自动生效：用 <c>[RuntimeInitializeOnLoadMethod]</c> 在 Play 时自己起一个隐藏宿主对象，
/// 因此**不需要改场景文件**（避免手改 .unity 的 YAML）。它只在
/// <c>AngryBirdsBefore</c> 这个场景里启动，所以同工程里的 After 版本不会互相污染。
///
/// 故意保留的反模式（编号与 Assets/PerfAgentSample/PERF-FAULTS.md 一致）：
///  F1  每帧 new List&lt;GameObject&gt;           → gc_collection_new
///  F2  每帧 GameObject.FindGameObjectsWithTag  → find_api
///  F3  每帧 LINQ Where/ToList/Any              → linq
///  F4  每帧 GetComponent&lt;Rigidbody2D&gt;       → getcomponent
///  F5  每帧 Camera.main                       → camera_main
///  F6  每帧字符串拼接                         → gc_string_concat
///  F7  每帧 string.Format                     → gc_string_format
///  F8  每帧 new StringBuilder                 → stringbuilder_new
///  F9  每帧 Debug.Log                         → debug_log（并把 Console 刷屏）
///  F10 每帧 new Vector2[16]                   → gc_array_new
///  F11 每帧 Physics2D.RaycastAll              → physics_alloc
///  F12 每帧 new GameObject + Destroy          → instantiate_destroy
///  F13 每 300 帧 GC.Collect()                 → gc_collect
///  F14 每帧 foreach 遍历 Dictionary           → foreach_enumerator（Info）
/// </summary>
public class BeforeTelemetryMonitor : MonoBehaviour
{
    /// <summary>本脚本只在自己那一版的场景里启动。</summary>
    public const string HostSceneName = "AngryBirdsBefore";

    /// <summary>统计出来的存活刚体数量，供外部读取（两版都有）。</summary>
    public int TrackedBodyCount { get; private set; }

    /// <summary>最近一次上报的文本（两版都有）。</summary>
    public string LastReport { get; private set; }

    readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    readonly Vector2[] history = new Vector2[32];   // 这个数组是复用的，本身没问题
    int historyCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (SceneManager.GetActiveScene().name != HostSceneName)
        {
            return;   // 不是这一版的场景，别插一脚
        }
        var host = new GameObject("[PerfTelemetry-Before]");
        host.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(host);
        host.AddComponent<BeforeTelemetryMonitor>();
    }

    void Update()
    {
        // F1 每帧分配一个 List
        var alive = new List<GameObject>();

        // F2 每帧全场景按 Tag 查找 + F3 每帧 LINQ
        var bricks = GameObject.FindGameObjectsWithTag("Brick").Where(b => b != null).ToList();
        bool anyPigLeft = GameObject.FindGameObjectsWithTag("Pig").Any();

        // F4 每帧 GetComponent（对查找结果逐个取刚体）
        for (int i = 0; i < bricks.Count; i++)
        {
            var body = bricks[i].GetComponent<Rigidbody2D>();
            if (body != null && body.velocity.sqrMagnitude > 0.05f)
            {
                alive.Add(bricks[i]);
            }
        }

        TrackedBodyCount = alive.Count + (anyPigLeft ? 1 : 0);

        // F5 每帧 Camera.main
        var cam = Camera.main;
        Vector3 camPosition = cam == null ? Vector3.zero : cam.transform.position;

        // F6 字符串拼接
        string line = "t=" + Time.time + " alive=" + TrackedBodyCount + " cam=" + camPosition.x;

        // F7 string.Format
        string detail = string.Format("bricks={0} pigs={1} birds={2}", bricks.Count, anyPigLeft, alive.Count);

        // F8 每帧 new StringBuilder（而不是复用字段）
        var builder = new StringBuilder();
        builder.Append(line).Append(" | ").Append(detail);
        LastReport = builder.ToString();

        // F9 每帧打日志（发布版本也不关）
        Debug.Log(LastReport);

        // F10 每帧分配数组
        var samples = new Vector2[16];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new Vector2(camPosition.x + i, camPosition.y + i * 0.5f);
        }
        for (int i = 0; i < samples.Length && historyCount < history.Length; i++)
        {
            history[historyCount++] = samples[i];
        }

        // F11 每帧非 NonAlloc 的物理查询
        var hits = Physics2D.RaycastAll(camPosition, Vector2.down, 5f);
        if (hits.Length > 0)
        {
            counts["hits"] = hits.Length;
        }

        // F12 每帧新建再销毁一个空对象（看不见，但分配与原生开销都在）
        var marker = new GameObject("perf-marker");
        marker.transform.position = camPosition;
        Destroy(marker, 0.1f);

        // F14 每帧 foreach 遍历 Dictionary
        foreach (var pair in counts)
        {
            if (pair.Value == 0) continue;
        }

        // F13 帧数到点就手动 GC，以为这样能治卡顿
        if (Time.frameCount % 300 == 0)
        {
            historyCount = 0;
            GC.Collect();
        }
    }
}

# 缺陷编号表（Before → After）

用来核对「PerfAgent 到底能不能报出来」，以及「After 到底修了什么」。
两份的脚本类名与文件名都带前缀（`BeforeXxx` / `AfterXxx`），下面按编号对照。

## 一、刻意注入的每帧反模式（脚本反模式扫描应当命中）

都在 `Before/Scripts/BeforeTelemetryMonitor.cs` 的 `Update()` 里，文件头注释也逐条标了编号。

| 编号 | Before 的写法（方法体内） | 扫描项 | After 的修法（`After/Scripts/AfterTelemetryMonitor.cs`） |
| --- | --- | --- | --- |
| F1 | `new List<GameObject>()` | `gc_collection_new` | 容器提升为字段 + `Clear()` 复用 |
| F2 | `GameObject.FindGameObjectsWithTag(...)` | `find_api` | 注册表低频刷新（60 帧一次），写在 `RefreshRegistry()` 里 |
| F3 | `.Where(...).ToList()`、`.Any()` | `linq` | 手写 `for` 循环，去掉迭代器/闭包 |
| F4 | `GetComponent<Rigidbody2D>()` | `getcomponent` | 注册表里缓存 `Rigidbody2D` 引用 |
| F5 | `Camera.main` | `camera_main` | `Awake` 里取一次存字段 |
| F6 | `"t=" + Time.time + ...` | `gc_string_concat` | 数值变化时才重建，且用 `StringBuilder` 追加 |
| F7 | `string.Format("bricks={0} ...")` | `gc_string_format` | 同上，`Append` 代替格式化 |
| F8 | `new StringBuilder()` | `stringbuilder_new` | `StringBuilder` 字段 + `Clear()` |
| F9 | `Debug.Log(...)` | `debug_log` | 删掉每帧日志（要留就放进非每帧路径） |
| F10 | `new Vector2[16]` | `gc_array_new` | 不再逐帧建数组 |
| F11 | `Physics2D.RaycastAll(...)` | `physics_alloc` | `Physics2D.RaycastNonAlloc` + 复用 `RaycastHit2D[]` |
| F12 | `new GameObject(...)` + `Destroy(...)` | `instantiate_destroy` | 标记对象只建一次，之后只改坐标 |
| F13 | `GC.Collect()` | `gc_collect` | 删除；减少分配才是解法 |
| F14 | `foreach (var p in dictionary)` | `foreach_enumerator`（Info） | 用 `List` + `for` 遍历 |

`Before/Scripts/BeforeGameManager.cs` 里另有两处最常见的「调完忘了删」：

| 编号 | Before | 扫描项 |
| --- | --- | --- |
| F15 | `Update()` 里 `Debug.Log("state=" + ... + " t=" + Time.time)` | `debug_log` + `gc_string_concat` |
| F16 | `OnGUI()` 里拼接 `"birds left: " + ...` 与 `string.Format(...)` | `gc_string_concat` + `gc_string_format` |

> After 版 `OnGUI` 只剩字面量标签，`Update` 不再打日志。

## 二、上游自带的每帧热路径（动态采集能看出来，静态扫描看不见）

这类写法的特点是**反模式藏在一个辅助方法里**，`Update` 只调了方法名，
而脚本反模式扫描只看「每帧方法体内部」——所以它抓不到，但每帧 GC 分配与帧时间会如实反映。
这正是「静态扫描 + 动态采集」互补的现场教材。

| 编号 | 位置（Before） | 问题 | After 的修法（文件同名，前缀 After） |
| --- | --- | --- | --- |
| G1 | `BeforeGameManager.BricksBirdsPigsStoppedMoving` | 每帧 `Bricks.Union(Birds).Union(Pigs)`（LINQ）+ 对每个对象 `GetComponent<Rigidbody2D>()` | `RefreshTrackedBodies()` 启动时缓存 `List<Rigidbody2D>`，每帧 `for` 遍历 + `null` 跳过 |
| G2 | `BeforeGameManager.AllPigsDestroyed` | `Pigs.All(x => x == null)`（闭包 + 迭代器） | 手写 `for` |
| G3 | `BeforeGameManager`（DOTween 回调/输入处理） | 反复读 `Camera.main` | 字段缓存，`Start` 取一次 |
| G4 | `BeforeSlingShot.DisplayTrajectoryLineRenderer2` | 拖拽期间每帧 `new Vector2[15]`；另有两个算完没用的局部变量 | 数组提升为字段复用；删掉死代码 |
| G5 | `BeforeSlingShot` | 每帧 `Camera.main.ScreenToWorldPoint` | 字段缓存 |
| G6 | `BeforeSlingShot` | `Mathf.Pow(t, 2)`、已废弃的 `SetVertexCount` | `t * t`、`positionCount` |
| G7 | `BeforeBird` | `Start/FixedUpdate/OnThrow` 反复 `GetComponent<>`；速度反复低于阈值时**重复启动**销毁协程；每次 `new WaitForSeconds` | `Awake` 缓存四个组件；`destroyScheduled` 只启动一次；`static readonly WaitForSeconds` |
| G8 | `BeforeBrick` / `BeforePig` | 每次碰撞 `GetComponent<Rigidbody2D>()`（还有 `AudioSource`/`SpriteRenderer`） | 用 `col.rigidbody`（就是撞过来的那个刚体，与 `col.gameObject.GetComponent<Rigidbody2D>()` 等价）；自身组件 `Awake` 缓存 |
| G9 | `BeforePig` / `BeforeDestroyer` | `col.gameObject.tag == "Bird"` / 取 `tag` 字符串再比较 | `CompareTag("Bird")` |
| G10 | `BeforeCameraPinchToZoom` | 双指缩放时每帧最多 5 次 `GetComponent<Camera>()` | `Awake` 缓存 `Camera`；没有双指时直接 return |
| G11 | `BeforeParallaxScrolling` | 私有字段名叫 `camera`，遮蔽基类已废弃的 `Component.camera`（CS0108 警告） | 改名 `parallaxCamera` |
| G12 | `BeforeGameManager`（重开一局） | `Application.LoadLevel(Application.loadedLevel)`（已废弃） | `SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex)` |

## 三、编译期就能看到的差别（离线 csc 校验，两份一起编）

| | Before | After |
| --- | --- | --- |
| 退出码 | 0 | 0 |
| 警告 | 4 条（`SetVertexCount`、`Application.LoadLevel`、`Application.loadedLevel` 已废弃 + CS0108） | **0 条** |

## 四、明确**没有**改的东西（避免把样例改成另一个游戏）

- 玩法、场景内容、预制体、美术与音效资源：完全一致（After 只是整体重发了 GUID 以便共存）。
- `Assets/PerfAgentSample/Plugins` 里的 DOTween 两份共用，两份脚本都还是调用 `DOMove`。
- Before 的 `BeforeGameManager` 依然用上游那套「列表 + 索引」推进小鸟，After 只是把**每帧成本**降下来。

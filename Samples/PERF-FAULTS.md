# 缺陷编号表（Before → After）

用来核对「PerfAgent 到底能不能报出来」，以及「After 到底修了什么」。
左列编号在 `AngryBirds_before/Assets/Scripts/TelemetryMonitor.cs` 的注释里逐条标注。

## 一、刻意注入的每帧反模式（脚本反模式扫描应当命中）

| 编号 | Before 的写法（方法体内） | 扫描项 | After 的修法 |
| --- | --- | --- | --- |
| F1 | `new List<GameObject>()` | `gc_collection_new` | 容器提升为字段 + `Clear()` 复用 |
| F2 | `GameObject.FindGameObjectsWithTag(...)` | `find_api` | 注册表低频刷新（60 帧一次），更新时不查找 |
| F3 | `.Where(...).ToList()`、`.Any()`、`.All(...)` | `linq` | 手写 `for` 循环，去掉迭代器/闭包 |
| F4 | `GetComponent<Rigidbody2D>()` | `getcomponent` | 注册表里缓存 `Rigidbody2D` 引用 |
| F5 | `Camera.main` | `camera_main` | `Awake` 里取一次存字段 |
| F6 | `"t=" + Time.time + ...` | `gc_string_concat` | 数值变化时才重建，且用 `StringBuilder` 追加 |
| F7 | `string.Format("bricks={0} ...")` | `gc_string_format` | 同上，`Append` 代替格式化 |
| F8 | `new StringBuilder()` | `stringbuilder_new` | `StringBuilder` 字段 + `Clear()` |
| F9 | `Debug.Log(...)` | `debug_log` | 删掉每帧日志（要留就放进非每帧路径） |
| F10 | `new Vector2[16]` | `gc_array_new` | 缓冲区提升为字段 |
| F11 | `Physics2D.RaycastAll(...)` | `physics_alloc` | `Physics2D.RaycastNonAlloc` + 复用 `RaycastHit2D[]` |
| F12 | `new GameObject(...)` + `Destroy(...)` | `instantiate_destroy` | 标记对象只建一次，之后只改坐标 |
| F13 | `GC.Collect()` | `gc_collect` | 删除；减少分配才是解法 |
| F14 | `foreach (var p in dictionary)` | `foreach_enumerator`（Info） | 用 `List` + `for` 遍历 |

同一份 `_before` 里的 `GameManager.cs` 还额外注入了两处最常见的「调完忘了删」：

| 编号 | Before | 扫描项 |
| --- | --- | --- |
| F15 | `Update` 里 `Debug.Log("state=" + ... + " t=" + Time.time)` | `debug_log` + `gc_string_concat` |
| F16 | `OnGUI` 里拼接 `"birds left: " + ...` 与 `string.Format(...)` | `gc_string_concat` + `gc_string_format` |

> After 里 `OnGUI` 只剩字面量标签，`Update` 不再打日志。

## 二、上游自带的每帧热路径（动态采集能看出来，静态扫描看不见）

这类写法的特点是**反模式藏在一个辅助方法里**，`Update` 只调了方法名，
而脚本反模式扫描只看「每帧方法体内部」，所以它抓不到——但每帧 GC 分配与帧时间会如实反映。
这正是「静态扫描 + 动态采集」互补的现场教材。

| 编号 | 位置 | Before 的问题 | After 的修法 |
| --- | --- | --- | --- |
| G1 | `GameManager.BricksBirdsPigsStoppedMoving` | 每帧 `Bricks.Union(Birds).Union(Pigs)`（LINQ，产生迭代器）并对每个对象 `GetComponent<Rigidbody2D>()` | 启动时缓存 `List<Rigidbody2D>`，每帧 `for` 遍历 + `null` 跳过 |
| G2 | `GameManager.AllPigsDestroyed` | `Pigs.All(x => x == null)`（闭包 + 迭代器） | 手写 `for` |
| G3 | `GameManager`（DOTween 回调、输入处理） | 反复读 `Camera.main` | 字段缓存，`Awake/Start` 取一次 |
| G4 | `SlingShot.DisplayTrajectoryLineRenderer2` | 拖拽期间每帧 `new Vector2[15]`；另有两个算完没用的局部变量 | 数组提升为字段复用；删掉死代码 |
| G5 | `SlingShot` | 每帧 `Camera.main.ScreenToWorldPoint` | 字段缓存 |
| G6 | `SlingShot` | `Mathf.Pow(t, 2)`、已废弃的 `SetVertexCount` | `t * t`、`positionCount` |
| G7 | `Bird` | `Start/FixedUpdate/OnThrow` 反复 `GetComponent<>`；速度反复低于阈值时**重复启动**销毁协程；每次 `new WaitForSeconds` | `Awake` 缓存四个组件；`destroyScheduled` 只启动一次；`static readonly WaitForSeconds` |
| G8 | `Brick` / `Pig` | 每次碰撞 `GetComponent<Rigidbody2D>()`（还有 `GetComponent<AudioSource>()`/`SpriteRenderer`） | 用 `col.rigidbody`（就是撞过来的那个刚体，与 `col.gameObject.GetComponent<Rigidbody2D>()` 等价）；自身组件 `Awake` 缓存 |
| G9 | `Pig` / `Destroyer` | `col.gameObject.tag == "Bird"` / 取 `tag` 字符串再比较 | `CompareTag("Bird")` |
| G10 | `CameraPinchToZoom` | 双指缩放时每帧最多 5 次 `GetComponent<Camera>()` | `Awake` 缓存 `Camera`；没有双指时直接 return |
| G11 | `ParallaxScrolling` | 私有字段名叫 `camera`，遮蔽基类已废弃的 `Component.camera`（CS0108 警告） | 改名 `parallaxCamera` |
| G12 | `GameManager`（重开一局） | `Application.LoadLevel(Application.loadedLevel)`（已废弃） | `SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex)` |

## 三、编译期就能看到的差别（离线 csc 校验）

| | Before | After |
| --- | --- | --- |
| 退出码 | 0 | 0 |
| 警告 | 4 条（`SetVertexCount`、`Application.LoadLevel`、`Application.loadedLevel` 已废弃 + CS0108） | **0 条** |

## 四、明确**没有**改的东西（避免把样例改成另一个游戏）

- 玩法、场景、预制体、美术与音效资源、`Constants`/`Enums`、DOTween 插件：完全一致。
- 所有脚本的 `.meta` 与 GUID 保持原样，场景里的组件引用不会断。
- Before 的 `GameManager` 依然用上游那套「列表 + 索引」推进小鸟，After 只是把**每帧成本**降下来。

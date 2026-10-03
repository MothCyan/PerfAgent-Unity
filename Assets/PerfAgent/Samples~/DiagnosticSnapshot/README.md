# Diagnostic Snapshot 示例

`sample-snapshot.json` 展示了 PerfAgent 快照的最小结构，以及帧耗时、托管分配和渲染指标如何进入统一数据模型。

导入示例后，可将该文件作为结构参考。实际快照由 PerfAgent 写入工程的 `ProjectSettings/PerfAgent/Snapshots/`，不应放入 `Assets/`。
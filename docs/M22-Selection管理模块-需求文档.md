# M22-Selection管理模块-需求文档

## 1. 模块职责

- **负责**：获取 Unity 编辑器当前选中的 GameObject 或资源列表；通过 InstanceID 或资源路径设置编辑器选中项；清空当前选中项。
- **不负责**：维护选中历史记录；执行复杂的组件筛选；处理 Hierarchy 窗口外的特定窗口（如 Animation 窗口）选中逻辑。
- **输入输出**：输入为来自 MCP Client 的 `selection.get/set/clear` 指令；输出为包含选中对象 ID、资源路径及激活对象详情的 JSON 数据。

## 2. 数据模型


| 字段名                   | 类型           | 语义              | 合法范围       | 默认值 |
| --------------------- | ------------ | --------------- | ---------- | --- |
| selectedGameObjectIds | list[int]    | 当前选中的场景对象 ID 列表 | >= 0 的整数列表 | []  |
| selectedAssetPaths    | list[string] | 当前选中的资源路径列表     | 有效的项目相对路径  | []  |
| activeGameObjectId    | int          | 当前主选中的对象 ID     | >= 0 的整数   | 0   |
| selectionCount        | int          | 选中项总数           | >= 0       | 0   |


## 3. CRUD 操作


| 操作      | 入口                | 禁用条件         | Command 类名        | Undo 语义           |
| ------- | ----------------- | ------------ | ----------------- | ----------------- |
| 获取当前选中项 | `selection.get`   | 无            | GetSelectionCmd   | 不进 CommandHistory |
| 设置选中项   | `selection.set`   | 传入 ID 或路径皆无效 | SetSelectionCmd   | 不进 CommandHistory |
| 清空选中项   | `selection.clear` | 已经为空         | ClearSelectionCmd | 不进 CommandHistory |


## 4. 交互规格

- **触发事件**：接收到 WebSocket `selection.`* 消息。
- **状态变化**：Unity 编辑器内部 Selection 状态实时更新。
- **数据提交时机**：`set/clear` 执行后立即同步编辑器；`get` 实时查询返回。
- **取消回退**：仅通过重新设置 Selection 实现。

## 5. 视觉规格

不涉及。

## 6. 校验规则


| 规则       | 检查时机       | 级别  | 提示文案             |
| -------- | ---------- | --- | ---------------- |
| 对象 ID 校验 | 执行 `set` 前 | 警告  | ID [x] 在当前场景中不存在 |
| 资源路径校验   | 执行 `set` 前 | 警告  | 资源路径 [y] 无效或不存在  |
| 参数非空校验   | 执行 `set` 时 | 错误  | 必须提供至少一个有效的选中目标  |


## 7. 跨模块联动


| 模块              | 方向   | 说明                     |
| --------------- | ---- | ---------------------- |
| M02 UnityBridge | 被动接收 | 接收来自 WebSocket 的选区操作指令 |
| M08 GameObject  | 主动通知 | 请求根据 ID 获取场景对象引用       |
| M12 Asset       | 主动通知 | 请求根据路径加载资源对象           |


## 8. 技术实现要点

- **核心类**：`SelectionManager` (逻辑入口), `SelectionRequest` (参数解析), `SelectionResponse` (数据封装)。
- **逻辑流**：`Receive SetCmd` -> `Resolve IDs/Paths` -> `UnityEditor.Selection.objects = targets` -> `Return Result`。
- **性能约束**：选中项超过 1000 个时需分批处理；查询耗时需在 50ms 内；减少对 `AssetDatabase` 的频繁扫描。

## 9. 验收标准

- [前置条件] -> [操作：调用 selection.get] -> [期望结果：返回当前 Hierarchy 选中的所有 ID]。
- [前置条件] -> [操作：调用 selection.set 并传入有效路径] -> [期望结果：Project 窗口自动选中该资源]。
- [前置条件] -> [操作：调用 selection.clear] -> [期望结果：编辑器内没有任何选中的对象]。
- [前置条件] -> [操作：选中多个物体后调用 selection.get] -> [期望结果：activeGameObjectId 正确反映主选中项]。

## 10. 边界规范

- **空数据**：`get` 返回空列表，`selectionCount` 为 0。
- **单元素**：`activeGameObjectId` 与 `selectedGameObjectIds[0]` 一致。
- **上下限临界值**：支持一次性设置 500 个以上对象选中，但不产生显著卡顿。
- **异常数据恢复**：若传入 ID 无效，则跳过该 ID 并继续处理其余有效 ID。

## 11. 周边可选功能

- **P1**：支持根据组件类型筛选选中项。
- **P2**：预留 `ping_selection` 功能，在编辑器内高亮闪烁选中物体。


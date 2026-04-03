# M12-Asset管理模块-需求文档

## 1. 模块职责

- 负责：
  - 在 Unity `Assets/` 目录下查找资源（按名称、类型、路径）。
  - 执行资源的基本文件操作：创建文件夹、复制、移动、删除。
  - 触发 Unity 资源数据库刷新（AssetDatabase.Refresh）。
  - 获取资源的元数据信息（GUID、类型、大小、修改时间）。
- 不负责：
  - 不负责修改资源内部数据（如编辑脚本代码、修改纹理像素）。
  - 不负责资源的导入设置详情修改（由专门模块负责）。
  - 不负责版本控制系统（Git/SVN）的操作。
- 输入/输出：
  - 输入：`asset.find`, `asset.createFolder`, `asset.copy`, `asset.move`, `asset.delete`, `asset.refresh`, `asset.getInfo` 命令。
  - 输出：资源列表、元数据详情、操作结果。

## 2. 数据模型


| 字段名          | 类型     | 语义          | 合法范围                        | 默认值 |
| ------------ | ------ | ----------- | --------------------------- | --- |
| assetPath    | string | 资源相对路径      | 必须以 `Assets/` 开头            | ""  |
| assetType    | string | 资源类名        | 如 `GameObject`, `Texture2D` | ""  |
| guid         | string | 资源全局唯一标识符   | 32 位 Hex 字符串                | ""  |
| fileSize     | long   | 字节数         | `[0, +∞)`                   | 0   |
| lastModified | long   | 最后修改时间戳（ms） | `>= 0`                      | 0   |


## 3. CRUD 操作


| 操作    | 入口                   | 禁用条件      | Command类名       | Undo语义            |
| ----- | -------------------- | --------- | --------------- | ----------------- |
| 查找资源  | `asset.find`         | 无         | FindAssetCmd    | 不进 CommandHistory |
| 创建文件夹 | `asset.createFolder` | 路径已存在     | CreateFolderCmd | 不进 CommandHistory |
| 复制资源  | `asset.copy`         | 源不存在/目标冲突 | CopyAssetCmd    | 不进 CommandHistory |
| 移动资源  | `asset.move`         | 源不存在/目标冲突 | MoveAssetCmd    | 不进 CommandHistory |
| 删除资源  | `asset.delete`       | 资源不存在     | DeleteAssetCmd  | 不进 CommandHistory |


## 4. 交互规格

- 触发事件：
  - 收到指令后通过 `AssetDatabase` API 执行对应操作。
- 状态变化：
  - `idle -> operating -> refreshing -> success/failed` |
- 数据提交时机：
  - 文件系统操作完成后立即调用 `AssetDatabase.SaveAssets()`。
- 取消/回退：
  - 资源删除为不可逆操作，需在指令层进行确认或通过 Git 恢复。

## 5. 视觉规格

不涉及。

## 6. 校验规则


| 规则     | 检查时机   | 级别      | 提示文案                            |
| ------ | ------ | ------- | ------------------------------- |
| 路径合法性  | 所有操作前  | Error   | `路径必须以 Assets/ 开头: {assetPath}` |
| 源文件存在  | 复制/移动前 | Error   | `源资源不存在: {assetPath}`           |
| 目标路径冲突 | 移动/创建前 | Error   | `目标路径已存在: {targetPath}`         |
| 资源锁定状态 | 删除/移动前 | Warning | `资源正在被其它编辑器进程使用`                |


## 7. 跨模块联动


| 模块                  | 方向   | 说明                         |
| ------------------- | ---- | -------------------------- |
| M02 UnityBridge通信模块 | 被动接收 | 转发资产管理相关的 WebSocket 指令     |
| M13 Prefab管理模块      | 主动通知 | 实例化 Prefab 前检查资产是否存在       |
| M18 脚本读写模块          | 主动通知 | 脚本文件变更后触发 AssetDatabase 刷新 |


## 8. 技术实现要点

- 关键类与职责：
  - `AssetManager`：封装 `AssetDatabase` 的核心业务逻辑。
  - `AssetInfoProvider`：提取文件的 GUID 和元数据。
  - `FileOperationGuard`：处理文件系统层面的冲突与权限校验。
- 核心流程：
  - `asset.move -> check paths -> AssetDatabase.MoveAsset -> SaveAssets -> Refresh -> return`。
- 性能约束：
  - 资源搜索（10000 个资源内）耗时 `< 100ms`。
  - 批量刷新操作需合并触发，避免频繁全量扫描。
  - 移动/删除大文件夹（>1GB）需支持异步回调。

## 9. 验收标准

1. [空场景] -> [调用 `asset.createFolder`] -> [工程面板出现新文件夹]。
2. [已有 Prefab] -> [调用 `asset.getInfo`] -> [返回正确的 GUID 及类型]。
3. [调用 `asset.find` 搜索名称] -> [返回包含所有匹配项的路径数组]。
4. [删除已存在资源] -> [调用 `asset.delete`] -> [文件从磁盘移除且工程视图同步更新]。

## 10. 边界规范

- 空数据：查找结果为空时返回空数组 `[]`。
- 单元素：仅对单个文件执行操作时，不触发全局目录重扫。
- 上下限临界值：路径长度接近系统限制（260 字符）时应进行警告。
- 异常数据恢复：操作失败时，尝试调用 `AssetDatabase.Refresh()` 同步工程状态。

## 11. 周边可选功能

- P1：资产依赖项查询，预留 `asset.getDependencies` 接口。
- P2：资产标签（Labels）管理，预留 `asset.setLabels` 字段。


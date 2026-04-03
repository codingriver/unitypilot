# M13-Prefab操作模块-需求文档

## 1. 模块职责
- 负责：
  - 将场景中的 GameObject 创建为 Prefab 资源。
  - 在场景中实例化 Prefab。
  - 进入与退出 Prefab 编辑模式。
  - 保存并同步对 Prefab 的修改。
- 不负责：
  - 不负责 Prefab 变体的深层继承关系自动映射。
  - 不负责嵌套 Prefab 的递归解包操作。
  - 不负责处理 Prefab 内部的特定业务组件逻辑。
- 输入/输出：
  - 输入：`prefab.create`、`prefab.instantiate`、`prefab.open`、`prefab.close`、`prefab.save` 命令。
  - 输出：`prefab.status` 事件、操作执行结果（成功/失败/路径）。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| prefabPath | string | Prefab 资源路径 | 以 `Assets/` 开头，`.prefab` 结尾 | "" |
| sourceGameObjectId | int | 源对象 InstanceID | 有效的对象 ID | 0 |
| instanceId | int | 实例化的对象 ID | 有效的对象 ID | 0 |
| isInPrefabMode | bool | 是否处于 Prefab 编辑模式 | `true/false` | false |
| isPrefabDirty | bool | Prefab 是否有未保存修改 | `true/false` | false |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 创建 Prefab | `prefab.create` | `sourceGameObjectId` 无效 | CreatePrefabCmd | 不进 CommandHistory |
| 实例化 Prefab | `prefab.instantiate` | `prefabPath` 不存在 | InstantiatePrefabCmd | 不进 CommandHistory |
| 打开 Prefab 编辑 | `prefab.open` | 已经在 Prefab 模式 | OpenPrefabCmd | 不进 CommandHistory |
| 关闭 Prefab 编辑 | `prefab.close` | 未在 Prefab 模式 | ClosePrefabCmd | 不进 CommandHistory |
| 保存 Prefab 修改 | `prefab.save` | `isPrefabDirty=false` | SavePrefabCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：接收到 `prefab.*` 指令后，通过 `PrefabUtility` 接口调用 Unity 引擎逻辑。
- 状态变化：`NormalMode -> PrefabMode (Editing) -> Saved/Closed`。
- 数据提交时机：
  - 创建或实例化成功后立即返回新对象的 InstanceID。
  - 进入/退出模式时同步 `isInPrefabMode` 状态。
- 取消/回退：操作失败时返回具体错误码，不支持通过 MCP 撤销已生成的资源文件。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 资源路径必须位于 Assets 下 | 收到创建/打开请求时 | Error | `路径非法：必须以 Assets/ 开头` |
| 目标路径已存在同名 Prefab | 执行 `prefab.create` 前 | Warning | `路径已存在，将执行覆盖操作` |
| 编辑模式必须匹配环境 | 执行 `prefab.save` 前 | Error | `当前未处于 Prefab 编辑模式，无法保存` |
| 源对象必须包含有效 Transform | 创建 Prefab 时 | Error | `对象无效：缺少 Transform 组件` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 转发 Prefab 操作指令至 Unity 侧 |
| M08 GameObject管理模块 | 主动通知 | 实例化或创建后通知 M08 更新对象树缓存 |
| M12 资源管理模块 | 主动通知 | Prefab 资源变更后触发 AssetDatabase 刷新 |

## 8. 技术实现要点
- 关键类与职责：
  - `PrefabService`：封装 `PrefabUtility` 提供核心生命周期管理功能。
  - `PrefabStageTracker`：监听 `PrefabStage` 事件，同步编辑状态。
  - `PrefabCommandExecutor`：处理 WS 指令与 C# 方法的异步映射。
- 核心流程：`prefab.create -> PrefabUtility.SaveAsPrefabAsset -> Refresh AssetDatabase -> Return path`。
- 性能约束：
  - 实例化操作响应时间 `< 100ms`（中等复杂度 Prefab）。
  - 保存操作必须在主线程执行，避免阻塞通信。
  - 大量实例化请求需支持队列处理。

## 9. 验收标准
1. [场景存在有效 GO] -> [调用 `prefab.create`] -> [Assets 目录下生成对应 .prefab 文件]。
2. [提供有效路径] -> [调用 `prefab.instantiate`] -> [场景中出现 Prefab 实例，返回 instanceId]。
3. [调用 `prefab.open`] -> [Unity 编辑器视图进入 Prefab 隔离编辑状态]。
4. [修改 Prefab 后调用 `prefab.save`] -> [文件磁盘内容更新，`isPrefabDirty` 变为 false]。

## 10. 边界规范
- 空数据：路径为空字符串时直接拦截，返回 `Path empty` 错误。
- 单元素：支持对仅包含单个 Transform 的空对象创建 Prefab。
- 上下限临界值：支持深达 100 层的嵌套对象结构创建 Prefab。
- 异常数据恢复：若 Prefab 模式异常卡死，提供强制退出 `prefab.close` 接口恢复编辑器。

## 11. 周边可选功能
- P1：Prefab 变体创建，预留 `variantSourcePath` 参数。
- P2：批量实例化支持，预留 `instantiate_batch` 接口。

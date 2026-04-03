# M10-Component操作模块-需求文档

## 1. 模块职责
- 负责：
  - 在指定 GameObject 上添加、移除组件。
  - 获取组件的序列化字段值及状态。
  - 修改组件的序列化属性和启用状态。
  - 列出 GameObject 挂载的所有组件及其类型。
- 不负责：
  - 不负责处理非序列化字段（如私有临时变量）。
  - 不负责组件内部的复杂业务逻辑执行。
  - 不负责资源生命周期管理。
- 输入/输出：
  - 输入：`component.add`, `component.remove`, `component.get`, `component.modify`, `component.list` 命令。
  - 输出：组件属性列表、操作成功/失败回执。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| gameObjectId | int | 目标物体 InstanceID | 有效物体 ID | 0 |
| componentType | string | 组件类型名称 | 程序集中存在的类型名 | "" |
| properties | dict | 序列化属性键值对 | 基础类型、Vector、Color 等 | {} |
| enabled | bool | 组件启用状态 | `true/false` | true |
| componentIndex | int | 同类型组件的索引 | `[0, +∞)` | 0 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 添加组件 | `component.add` | 物体不存在 | AddComponentCmd | 不进 CommandHistory |
| 移除组件 | `component.remove` | 组件不存在/不可销毁 | RemoveComponentCmd | 不进 CommandHistory |
| 获取属性 | `component.get` | 组件不存在 | GetComponentPropertiesCmd | 不进 CommandHistory |
| 修改属性 | `component.modify` | 组件不存在/属性不可写 | ModifyComponentPropertiesCmd | 不进 CommandHistory |
| 列出组件 | `component.list` | 物体不存在 | ListComponentsCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：
  - 收到指令后定位 GameObject，通过反射或 SerializedObject 访问组件。
- 状态变化：
  - `idle -> processing -> success/failed`。
- 数据提交时机：
  - 修改操作在 `SerializedObject.ApplyModifiedProperties()` 后立即生效并返回。
- 取消/回退：
  - MVP 阶段不提供自动回滚，由 AI 记录前态进行反向操作。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 类型必须存在 | 添加组件前 | Error | `无法找到组件类型: {componentType}` |
| 属性键值合法性 | 修改属性前 | Warning | `属性 {key} 不存在或不可写入` |
| 禁止移除核心组件 | 移除组件前 | Error | `Transform 组件无法被移除` |
| InstanceID 有效性 | 所有操作前 | Error | `未找到指定的 GameObject (ID: {gameObjectId})` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 接收组件操作命令并回传结果 |
| M08 GameObject管理模块 | 主动通知 | 请求定位指定 ID 的物体实例 |
| M01 编排服务模块 | 被动接收 | 提供原子化的组件操作能力 |

## 8. 技术实现要点
- 关键类与职责：
  - `ComponentService`：组件增删查改逻辑封装。
  - `PropertySerializer`：处理 Unity 属性与 JSON 之间的转换。
  - `TypeResolver`：快速定位 C# 类名对应的 System.Type。
- 核心流程：
  - `component.modify -> find object -> find component -> SerializedObject access -> apply -> result`。
- 性能约束：
  - 单个组件属性查询耗时 `< 20ms`。
  - 批量修改 10 个属性内响应 `< 50ms`。
  - 避免在主线程进行耗时的递归反射搜索。

## 9. 验收标准
1. [物体已选中] -> [调用 `component.add` 添加 `Light`] -> [物体成功挂载 Light 组件]。
2. [已有 MeshRenderer] -> [调用 `component.modify` 修改 `enabled: false`] -> [组件被禁用，渲染消失]。
3. [物体无 Camera] -> [调用 `component.get`] -> [返回错误：组件不存在]。
4. [调用 `component.list`] -> [返回该物体下所有组件名数组，包含 Transform]。

## 10. 边界规范
- 空数据：属性字典为空时，`modify` 不执行任何操作并返回成功。
- 单元素：移除物体上唯一的该类组件时，正常销毁。
- 上下限临界值：对 `Vector3` 修改极大值时，遵循 Unity 浮点数限制。
- 异常数据恢复：序列化失败时，中断操作并记录 `SerializationException`。

## 11. 周边可选功能
- P1：支持批量属性修改，预留 `batchProperties` 数组接口。
- P2：组件拷贝功能，预留 `component.copyTo` 接口。
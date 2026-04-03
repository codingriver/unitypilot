# M08-GameObject操作模块-需求文档

## 1. 模块职责
- 负责：
  - 在 Unity 场景层级中创建、查找、获取 GameObject。
  - 修改 GameObject 的基础属性（名称、标签、层级、激活状态、静态标记）。
  - 执行 GameObject 的克隆、销毁、重父级及变换操作（位置、旋转、缩放）。
- 不负责：
  - 不负责 GameObject 上挂载的 Component 内部数据的深度编辑。
  - 不负责 Prefab 资产的实例化（由资源模块负责）。
  - 不负责物理引擎相关的运行时模拟。
- 输入/输出：
  - 输入：`gameobject.create`、`gameobject.modify` 等命令。
  - 输出：`GameObjectInfo` 对象、操作结果。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| instanceId | int | 对象唯一标识符 | 非零整数 | 0 |
| name | string | 对象显示名称 | 长度 `[1, 256]` | "New GameObject" |
| tag | string | 对象标签 | Unity 预设标签 | "Untagged" |
| layer | int | 对象所属物理层 | `[0, 31]` | 0 |
| activeSelf | bool | 自身激活状态 | `true/false` | true |
| isStatic | bool | 是否为静态对象 | `true/false` | false |
| transform | object | 变换组件信息 | 包含 pos/rot/scale | (0,0,0) |
| parentId | int | 父对象 ID | 存在或 0 (根级) | 0 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 创建对象 | `gameobject.create` | 资源受限 | CreateGameObjectCmd | 不进 CommandHistory |
| 查找对象 | `gameobject.find` | 搜索条件为空 | FindGameObjectCmd | 不进 CommandHistory |
| 修改属性 | `gameobject.modify` | 只读资源 | ModifyGameObjectCmd | 不进 CommandHistory |
| 销毁对象 | `gameobject.delete` | 系统级受限对象 | DeleteGameObjectCmd | 不进 CommandHistory |
| 变换操作 | `gameobject.move` | 挂载锁定 | MoveGameObjectCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：
  - 收到 `gameobject.create` 后在场景中即时实例化。
- 状态变化：
  - 对象层级结构变化、激活状态切换。
- 数据提交时机：
  - 修改属性后立即调用 Unity API 并返回。
- 取消/回退：
  - 服务器侧不支持撤销；用户需通过后续命令反向操作。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 名称非法字符 | 写入 name 时 | Warning | `建议不要使用特殊字符：{name}` |
| 标签不存在 | 写入 tag 时 | Error | `Unity 中未定义的标签：{tag}` |
| 循环嵌套父子级 | 设置父级时 | Error | `无法将对象设为其自身的子对象` |
| ID 不匹配 | 执行 modify 时 | Error | `未找到 instanceId={id} 的对象` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 转发 GameObject 操作指令 |
| M09 Scene管理模块 | 主动通知 | 对象创建或销毁时同步场景脏标记 |
| M10 Component模块 | 被动接收 | 提供对象引用以执行组件查询 |

## 8. 技术实现要点
- 关键类与职责：
  - `GameObjectManager`：维护场景内对象引用缓存。
  - `TransformHandler`：处理复杂的坐标系转换逻辑。
  - `GameObjectFactory`：负责新对象的创建与初始属性应用。
- 核心流程：
  - `gameobject.create -> check parent -> instantiate -> set attributes -> return info`。
- 性能约束：
  - 查找单体对象（按 ID）耗时 `< 5ms`。
  - 全场景按名称模糊搜索耗时 `< 100ms`（10000 节点内）。
  - 创建 10 个以上对象时建议采用批量处理接口。

## 9. 验收标准
1. [空场景] -> [调用 `gameobject.create`] -> [场景中出现新对象且返回正确 ID]。
2. [已有对象] -> [调用 `gameobject.modify` 改名] -> [Unity 编辑器中显示名称同步更新]。
3. [多级层级] -> [调用 `gameobject.reparent`] -> [对象在 Hierarchy 中移动到新父节点下]。
4. [选中对象] -> [调用 `gameobject.delete`] -> [对象被销毁且后续查询该 ID 返回 null]。

## 10. 边界规范
- 空数据：查找无果时返回 `null` 或 `error: not_found`。
- 单元素：修改单个属性时只更新该字段，不覆盖其他数据。
- 上下限临界值：层级深度达到 100 层时应返回警告提示。
- 异常数据恢复：修改过程中 Unity 崩溃则在 Bridge 重连后自动重置状态。

## 11. 周边可选功能
- P1：批量属性同步，预留 `gameobject.batch.modify` 接口。
- P2：搜索结果排序，预留 `sort_by` 与 `order` 参数。

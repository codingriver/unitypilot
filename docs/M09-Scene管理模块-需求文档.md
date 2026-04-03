# M09-Scene管理模块-需求文档

## 1. 模块职责
- 负责：
  - Unity 场景的打开、关闭、保存及新建操作。
  - 管理多场景加载（叠加模式与单场景模式）。
  - 获取当前打开场景的元数据（路径、脏标记、索引）。
  - 设置场景中的激活状态。
- 不负责：
  - 不负责场景内具体的资产（GameObject）内容搜索（由 M08 负责）。
  - 不负责 Build Settings 中场景列表的配置。
  - 不负责场景资源的磁盘文件移动。
- 输入/输出：
  - 输入：`scene.open`、`scene.save` 等命令。
  - 输出：`SceneInfo` 对象、场景列表、操作状态。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| scenePath | string | 场景资源绝对路径 | Assets/ 开始的路径 | "" |
| sceneName | string | 场景显示名称 | 字符串 | "Untitled" |
| buildIndex | int | 构建索引 | `[-1, +∞)` | -1 |
| isLoaded | bool | 是否已加载 | `true/false` | false |
| isDirty | bool | 是否有未保存修改 | `true/false` | false |
| rootCount | int | 根节点数量 | `[0, +∞)` | 0 |
| isActive | bool | 是否为当前激活场景 | `true/false` | false |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 新建场景 | `scene.create` | 正在编译中 | CreateSceneCmd | 不进 CommandHistory |
| 打开场景 | `scene.open` | 路径无效 | OpenSceneCmd | 不进 CommandHistory |
| 保存场景 | `scene.save` | 只读文件 | SaveSceneCmd | 不进 CommandHistory |
| 加载场景 | `scene.load` | 已处于加载状态 | LoadSceneCmd | 不进 CommandHistory |
| 设置激活 | `scene.setActive` | 场景未加载 | SetActiveSceneCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：
  - 收到 `scene.open` 时触发 Unity `EditorSceneManager.OpenScene`。
- 状态变化：
  - 场景从 `unloaded -> loaded`，激活状态切换。
- 数据提交时机：
  - 场景保存动作成功后返回 `isDirty=false` 状态。
- 取消/回退：
  - 属于引擎级破坏性操作，由 Unity 原生保存确认框拦截（可选）。

## 5. 视觉规格
不涉及。

## 6.校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 路径非法 | 准备打开场景时 | Error | `未找到指定的场景资源：{path}` |
| 未保存强制覆盖 | 切换场景时 | Warning | `当前场景未保存，操作可能导致丢失数据` |
| 设置未加载场景 | 执行 setActive 时 | Error | `无法激活尚未加载的场景` |
| 重复保存 | 执行 save 时 | Info | `场景已保存过，无需重复操作` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 转发场景管理指令 |
| M08 GameObject操作模块 | 主动通知 | 场景切换后触发 GameObject 缓存刷新 |
| M01 编排服务模块 | 被动接收 | 返回当前场景列表以供 AI 全局感知 |

## 8. 技术实现要点
- 关键类与职责：
  - `SceneManagerWrapper`：封装 `EditorSceneManager` 调用。
  - `SceneStateStore`：维护当前多场景加载状态。
  - `SceneEventPublisher`：发布场景加载/卸载事件通知。
- 核心流程：
  - `scene.open -> check isDirty -> ask user (if mode) -> load async -> notify M08 -> reply`。
- 性能约束：
  - 获取 10 个以内已加载场景列表耗时 `< 10ms`。
  - 场景切换耗时受 Unity 资产加载影响，Bridge 侧超时设为 30s。
  - `scene.save` 操作不应阻塞 WS 心跳维持。

## 9. 验收标准
1. [空闲状态] -> [调用 `scene.list`] -> [返回当前所有已打开场景的元数据列表]。
2. [已有未保存修改] -> [调用 `scene.save`] -> [Unity 中场景脏标记消失，返回成功]。
3. [多场景环境] -> [调用 `scene.load` (Additive)] -> [新场景叠加到当前层级中]。
4. [选中非激活场景] -> [调用 `scene.setActive`] -> [该场景在 Hierarchy 中变为粗体显示]。

## 10. 边界规范
- 空数据：无场景加载时返回默认的空列表 `[]`。
- 单元素：始终至少存在一个激活场景（即使是 Untitled）。
- 上下限临界值：处理具有 50000+ 对象的大场景打开操作时的超时保护。
- 异常数据恢复：保存失败后自动尝试备份到 Temp 目录。

## 11. 周边可选功能
- P1：场景加载进度监控，预留 `scene.load.progress` 事件。
- P2：快照预览生成，预留 `scene.thumbnail.url` 接口。

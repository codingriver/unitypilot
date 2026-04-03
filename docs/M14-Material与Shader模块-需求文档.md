# M14-Material与Shader模块-需求文档

## 1. 模块职责
- 负责：
  - 创建新的材质（Material）资源并指定着色器（Shader）。
  - 修改材质属性，如颜色、纹理、数值、向量。
  - 将材质分配给场景中 GameObject 的渲染器组件。
  - 查询材质详情及可用 Shader 列表。
- 不负责：
  - 不负责 Shader 代码的编写与编译纠错。
  - 不负责着色器图（ShaderGraph）的逻辑编辑。
  - 不负责全局照明（GI）或光照贴图的烘焙配置。
- 输入/输出：
  - 输入：`material.create`、`material.modify`、`material.assign`、`material.get`、`shader.list`。
  - 输出：材质属性字典、Shader 列表数组、操作状态响应。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| materialPath | string | 材质资源路径 | 以 `Assets/` 开头，`.mat` 结尾 | "" |
| shaderName | string | 着色器名称 | 有效的 Shader ID | "Standard" |
| properties | dict | 属性键值对 | 包含 `type` 与 `value` | {} |
| targetGameObjectId | int | 目标对象 InstanceID | 有效的对象 ID | 0 |
| materialIndex | int | 渲染器材质索引 | `>= 0` | 0 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 创建材质 | `material.create` | `shaderName` 不存在 | CreateMaterialCmd | 不进 CommandHistory |
| 修改材质属性 | `material.modify` | 材质文件被锁定或只读 | ModifyMaterialCmd | 不进 CommandHistory |
| 分配材质 | `material.assign` | `targetGameObjectId` 无渲染器 | AssignMaterialCmd | 不进 CommandHistory |
| 获取材质详情 | `material.get` | 路径无效 | GetMaterialInfoCmd | 不进 CommandHistory |
| 获取 Shader 列表 | `shader.list` | 无 | ListShadersCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：执行指令后，通过 `Material` 类接口动态修改或通过 `AssetDatabase` 持久化材质。
- 状态变化：`Initial -> Modified -> Assigned -> Persistent`。
- 数据提交时机：
  - 创建材质后立即返回路径与 InstanceID。
  - 修改属性后立即调用 `EditorUtility.SetDirty` 同步编辑器。
- 取消/回退：属性修改通过覆盖旧值实现，不保留快照。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| Shader 名称必须系统存在 | 创建材质时 | Error | `Shader {name} 未找到` |
| 属性类型必须匹配 | 修改属性时 | Error | `属性 {prop} 类型错误：期望 {type}` |
| 纹理路径必须为有效资产 | 设置贴图时 | Warning | `纹理资源路径未找到，将设为空` |
| 目标索引超出渲染器范围 | 分配材质时 | Error | `Material Index {index} 越界` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 接收并解析材质/Shader 相关 RPC |
| M10 组件管理模块 | 主动通知 | 分配材质后通知 M10 刷新 Renderer 组件视图 |
| M12 资源管理模块 | 主动通知 | 材质创建与属性保存时触发 AssetDatabase 刷新 |

## 8. 技术实现要点
- 关键类与职责：
  - `MaterialService`：处理材质创建、属性注入与渲染器分配逻辑。
  - `ShaderInformer`：扫描并过滤系统已加载的所有着色器信息。
  - `PropertyConverter`：将 JSON 格式的颜色/向量转换为 Unity 对象。
- 核心流程：`material.modify -> Parse properties -> SetMaterialProperty -> EditorUtility.SetDirty -> Sync UI`。
- 性能约束：
  - 大规模材质属性更新单次延迟 `< 100ms`。
  - Shader 列表缓存机制，避免频繁扫描。
  - 支持对共享材质（SharedMaterial）的独立修改。

## 9. 验收标准
1. [输入有效路径与 Shader] -> [调用 `material.create`] -> [生成材质资产文件]。
2. [指定颜色属性 (1,0,0,1)] -> [调用 `material.modify`] -> [材质在编辑器中变为红色]。
3. [提供实例 ID] -> [调用 `material.assign`] -> [对应物体的 MeshRenderer 材质槽位更新]。
4. [调用 `shader.list`] -> [返回包含 Standard、Universal Render Pipeline/Lit 等的数组]。

## 10. 边界规范
- 空数据：空属性字典不执行任何修改。
- 单元素：支持只修改一个 Float 属性。
- 上下限临界值：处理 100+ 属性的复杂 Shader 材质无明显延迟。
- 异常数据恢复：属性类型异常时强制不保存并返回错误，保持材质原有状态。

## 11. 周边可选功能
- P1：材质球预览图生成，预留 `material.preview` 接口。
- P2：支持 MaterialPropertyBlock 操作，预留 `usePropertyBlock` 参数。

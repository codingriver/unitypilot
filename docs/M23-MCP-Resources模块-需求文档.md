# M23-MCP-Resources模块-需求文档

## 1. 模块职责
- **负责**：将 Unity 编辑器数据（如场景树、控制台日志、资源元数据）映射为 MCP 资源 URI；提供资源内容的只读访问；支持对 URI 进行分类浏览。
- **不负责**：提供资源的写入操作；执行耗时的重型扫描（如全盘资源哈希）；管理 MCP 服务器本身。
- **输入输出**：输入为 MCP Client 对 URI 的 read 请求；输出为格式化的 JSON 文本及 mimeType。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| :--- | :--- | :--- | :--- | :--- |
| resourceUri | string | 资源唯一定义符 | 以 unity:// 开头的 URI | "" |
| content | string | 序列化后的资源内容 | 有效的 JSON 文本 | "{}" |
| mimeType | string | 内容格式标识 | application/json | "application/json" |
| lastUpdated | long | 内容最后更新时间戳 | 毫秒级正整数 | 0 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command 类名 | Undo 语义 |
| :--- | :--- | :--- | :--- | :--- |
| 注册资源 URI | `Internal` | 无 | RegisterResourceCmd | 不进 CommandHistory |
| 查询资源内容 | `MCP Read` | URI 未注册 | ReadResourceCmd | 不进 CommandHistory |
| 刷新资源缓存 | `Internal` | 无 | RefreshResourceCmd | 不进 CommandHistory |

## 4. 交互规格
- **触发事件**：接收到来自 MCP Server 的 `list_resources` 或 `read_resource` 请求。
- **状态变化**：无，仅读取。
- **数据提交时机**：请求时实时触发 UnityBridge 查询。
- **取消回退**：无。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| :--- | :--- | :--- | :--- |
| URI 格式校验 | 请求处理前 | 错误 | 不支持该 URI 模式 |
| 资源存在性校验 | 执行查询时 | 错误 | 找不到 URI 对应的 Unity 数据源 |
| 内容长度校验 | 返回结果前 | 警告 | 资源内容过大，已截断 |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| :--- | :--- | :--- |
| M01 编排服务 | 被动接收 | 接收统一的数据同步调度 |
| M08 GameObject | 主动通知 | 请求 Hierarchy 场景树数据 |
| M12 Asset | 主动通知 | 请求资源元数据详情 |

## 8. 技术实现要点
- **核心类**：`ResourceManager` (URI 路由), `ResourceProvider` (数据转换), `CacheStore` (临时缓存)。
- **逻辑流**：`Read unity://console/logs` -> `Call M07` -> `Format JSON` -> `Return Resource`。
- **性能约束**：响应时间需在 200ms 以内；大资源（如 assets）需支持目录级的延迟加载；禁止在主线程同步读取大文件。

## 9. 验收标准
- [前置条件] -> [操作：访问 unity://scenes/hierarchy] -> [期望结果：返回完整的 GameObject 嵌套 JSON]。
- [前置条件] -> [操作：访问 unity://console/logs] -> [期望结果：返回最近 50 条控制台输出]。
- [前置条件] -> [操作：访问不存在的 URI] -> [期望结果：返回 Resource NotFound 错误]。
- [前置条件] -> [操作：注册新资源后重启] -> [期望结果：列表接口能查看到新增的 URI 模式]。

## 10. 边界规范
- **空数据**：返回空 JSON 对象 `{}` 或空数组 `[]`。
- **单元素**：若 URI 对应单个资产，返回该资产的元数据对象。
- **上下限临界值**：Console 日志最大返回 100 条。
- **异常数据恢复**：若 UnityBridge 超时，返回 504 错误并尝试重连。

## 11. 周边可选功能
- **P1**：支持资源订阅（Resource Templates），允许 URI 中带变量（如 `{path}`）。
- **P2**：预留 `binary_resource` 支持，允许直接通过 URI 读取二进制图片内容。

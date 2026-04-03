# M07-Console日志读取模块-需求文档

## 1. 模块职责
- 负责：
  - 读取 Unity 编辑器控制台的结构化日志（Log/Warning/Error/Exception/Assert）。
  - 支持按日志类型过滤和结果数量限制。
  - 提供清空控制台日志的功能。
- 不负责：
  - 不负责日志的持久化存储。
  - 不负责运行时自定义日志框架的注入。
  - 不负责日志文件（.log）的外部解析。
- 输入/输出：
  - 输入：`console.logs.get`、`console.clear` 命令。
  - 输出：`LogEntry` 列表、操作成功确认。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| logType | string | 日志级别 | `Log/Warning/Error/Exception/Assert` | "Log" |
| message | string | 日志正文内容 | 字符串 | "" |
| stackTrace | string | 堆栈信息 | 字符串 | "" |
| timestamp | long | 发生时间戳（ms） | `>= 0` | 0 |
| count | int | 折叠后的重复次数 | `[1, +∞)` | 1 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 获取日志列表 | `console.logs.get` | 无 | GetConsoleLogsCmd | 不进 CommandHistory |
| 清空控制台 | `console.clear` | PlayMode 锁定（可选） | ClearConsoleCmd | 不进 CommandHistory |
| 实时日志订阅 | WS 事件流 | 尚未连接 | SubscribeConsoleLogCmd | 不进 CommandHistory |
| 更新日志过滤器 | 内部状态 | 无 | UpdateLogFilterCmd | 不进 CommandHistory |
| 标记日志已读 | 内部状态 | 无 | MarkLogAsReadCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：
  - 收到 `console.logs.get` 时，Unity 侧调用 `LogEntries.GetEntries`。
- 状态变化：
  - `idle -> fetching -> data_returned`。
- 数据提交时机：
  - 调用 `console.clear` 成功后立即返回。
  - `console.logs.get` 在扫描完内存缓存后一次性返回数组。
- 取消/回退：
  - 属于只读或破坏性清除操作，不支持取消。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 获取条数超限 | 收到请求时 | Warning | `请求条数超过上限，已重置为 1000 条` |
| 非法日志类型过滤 | 收到请求时 | Error | `未知的日志类型：{type}` |
| 通信超时 | 等待 Unity 回包时 | Error | `读取控制台日志超时` |
| 权限不足 | 调用 clear 时 | Error | `当前权限级别无法执行清空操作` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 透传获取和清空日志的指令 |
| M04 自动修复循环模块 | 主动通知 | 为修复逻辑提供运行时错误上下文 |
| M01 编排服务模块 | 被动接收 | 返回结构化日志供 AI 决策参考 |

## 8. 技术实现要点
- 关键类与职责：
  - `ConsoleReader`：封装 Unity 内部 `LogEntries` API。
  - `LogFilter`：负责日志类型的按位掩码过滤逻辑。
  - `ConsoleService`：处理请求分发与结果脱敏。
- 核心流程：
  - `console.logs.get -> apply filters -> slice by count -> format entries -> reply`。
- 性能约束：
  - 单次读取 100 条日志响应时间 `< 80ms`。
  - 严禁在主线程循环中持续全量扫描控制台。
  - `console.clear` 调用耗时 `< 30ms`。

## 9. 验收标准
1. [控制台存在 Error] -> [调用 `console.logs.get` 带过滤] -> [仅返回 Error 类型的条目]。
2. [控制台有大量重复日志] -> [调用 `console.logs.get`] -> [返回条目的 count 字段反映重复数]。
3. [点击清空] -> [调用 `console.clear`] -> [Unity 控制台清空且后续查询返回空列表]。
4. [运行时发生 Exception] -> [调用 `console.logs.get`] -> [stackTrace 字段包含有效的 C# 堆栈]。

## 10. 边界规范
- 空数据：控制台无内容时返回 `[]`。
- 单元素：仅一条日志时返回长度为 1 的数组。
- 上下限临界值：请求 0 条或负数条时强制返回最新 1 条。
- 异常数据恢复：Unity API 失效时返回系统级 Error 并提示重启 Bridge。

## 11. 周边可选功能
- P1：实时推送模式，预留 `console.log` 事件接口。
- P2：日志导出功能，预留 `console.export.path` 字段。

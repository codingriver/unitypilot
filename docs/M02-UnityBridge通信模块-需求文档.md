# M02-UnityBridge通信模块-需求文档

## 1. 模块职责

- 负责：
  - 在 Unity 2022 Editor 内启动 WebSocket Client，并连接 Python 编排服务。
  - 接收命令并调用 Unity Editor API 执行。
  - 上报 `result/event/error/heartbeat`。
  - 维护连接重试和会话握手。
- 不负责：
  - 不负责 Agent 策略决策。
  - 不负责代码文件改写。
  - 不负责构建发布流程。
- 输入/输出：
  - 输入：编排服务下发命令。
  - 输出：命令执行结果、编辑器状态事件。

## 2. 数据模型


| 字段名                    | 类型     | 语义             | 合法范围                    | 默认值                   |
| ---------------------- | ------ | -------------- | ----------------------- | --------------------- |
| bridgeEnabled          | bool   | Bridge 是否启用    | `true/false`            | true                  |
| serverUrl              | string | WebSocket 服务地址 | `ws://127.0.0.1:{port}` | "ws://127.0.0.1:8765" |
| connectRetryIntervalMs | int    | 重连间隔           | `[1000, 10000]`         | 2000                  |
| handshakeTimeoutMs     | int    | 握手超时           | `[1000, 20000]`         | 5000                  |
| sessionId              | string | 当前桥接会话 ID      | 非空 UUID                 | ""                    |
| isAuthenticated        | bool   | 握手是否完成         | `true/false`            | false                 |
| lastHeartbeatAt        | long   | 上次心跳时间戳（ms）    | `>= 0`                  | 0                     |


## 3. CRUD 操作


| 操作        | 入口             | 禁用条件                  | Command类名                 | Undo语义            |
| --------- | -------------- | --------------------- | ------------------------- | ----------------- |
| 创建连接      | Editor 启动后自动触发 | `bridgeEnabled=false` | ConnectBridgeCmd          | 不进 CommandHistory |
| 更新连接状态    | 心跳/重连回调        | 未创建连接对象               | UpdateBridgeStateCmd      | 不进 CommandHistory |
| 关闭连接      | Editor 退出或手动关闭 | 连接已断开                 | CloseBridgeCmd            | 不进 CommandHistory |
| 创建命令处理器映射 | 初始化阶段          | 命令名重复注册               | RegisterCommandHandlerCmd | 不进 CommandHistory |
| 删除命令处理器映射 | 卸载模块           | 命令不存在                 | RemoveCommandHandlerCmd   | 不进 CommandHistory |


## 4. 交互规格

- 触发事件：
  - `InitializeOnLoad` 启动连接。
  - 收到命令后进入处理函数。
- 状态变化：
  - `disconnected -> connecting -> connected -> authenticated`。
- 数据提交时机：
  - 命令执行结束后立即回包。
  - 心跳按 `heartbeatIntervalMs` 周期发送。
- 取消/回退：
  - 命令执行中断线时，返回 `connection_lost` 错误并终止当前命令。

## 5. 视觉规格

不涉及。

## 6. 校验规则


| 规则                 | 检查时机  | 级别      | 提示文案                 |
| ------------------ | ----- | ------- | -------------------- |
| 仅接受已注册命令           | 收到命令时 | Error   | `未注册命令：{name}`       |
| `payload` 参数类型必须匹配 | 命令执行前 | Error   | `参数类型错误：{field}`     |
| 未认证会话不得执行业务命令      | 握手完成前 | Error   | `会话未认证`              |
| 重连间隔不得低于 1000ms    | 配置加载时 | Warning | `重连间隔过小，已回退为 1000ms` |


## 7. 跨模块联动


| 模块                  | 方向   | 说明                    |
| ------------------- | ---- | --------------------- |
| M01 编排服务模块          | 被动接收 | 接收命令并回传执行结果           |
| M03 编译与错误收集模块       | 主动通知 | 转发编译状态与错误事件           |
| M05 PlayMode与输入模拟模块 | 主动通知 | 转发 PlayMode 与鼠标输入执行结果 |


## 8. 技术实现要点

- 关键类与职责：
  - `EditorWsBridge`：连接管理与消息收发。
  - `BridgeCommandRouter`：命令路由到具体 handler。
  - `BridgeEventPublisher`：统一上报事件。
- 核心流程：
  - `on open -> send session.hello -> auth ok -> receive command -> execute -> send result`。
- 性能约束：
  - 每秒消息处理能力不少于 200 条。
  - 主线程单次消息处理时间 `< 8ms`。
  - 禁止在 `Update` 每帧重复创建 JSON 序列化对象。

## 9. 验收标准

1. [Unity 启动] -> [Bridge 初始化] -> [10 秒内连接并完成 `session.hello`]。
2. [发送已注册命令] -> [Bridge 执行] -> [返回 `result` 且 `id` 一致]。
3. [发送未知命令] -> [Bridge 路由] -> [返回 `未注册命令` 错误]。
4. [断网后恢复] -> [等待重连] -> [Bridge 自动重连并恢复可用状态]。

## 10. 边界规范

- 空数据：命令 `payload` 为空时，若命令允许空参数则执行，否则返回参数缺失错误。
- 单元素：仅有一个命令处理器时，路由仍需走统一映射逻辑。
- 上下限临界值：`connectRetryIntervalMs=1000` 与 `10000` 均可稳定重连。
- 异常数据恢复：JSON 解析失败后不崩溃，记录错误并继续监听下一条消息。

## 11. 周边可选功能

- P1：协议版本协商，预留 `protocolVersion` 字段和协商命令。
- P2：消息压缩传输，预留 `compression` 配置开关。


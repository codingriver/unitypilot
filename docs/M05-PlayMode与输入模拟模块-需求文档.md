# M05-PlayMode与输入模拟模块-需求文档

## 1. 模块职责

- 负责：
  - 控制 Unity Editor PlayMode 运行与停止。
  - 在指定编辑器窗口注入鼠标事件（左/中/右键）。
  - 提供输入执行结果与状态反馈。
- 不负责：
  - 不负责编译流程。
  - 不负责键盘宏录制回放。
  - 不负责运行时设备输入系统测试。
- 输入/输出：
  - 输入：`playmode.set`、`mouse.event` 命令。
  - 输出：PlayMode 状态变化事件、鼠标执行结果。

## 2. 数据模型


| 字段名           | 类型     | 语义              | 合法范围                                    | 默认值     |
| ------------- | ------ | --------------- | --------------------------------------- | ------- |
| isPlaying     | bool   | 当前是否处于 PlayMode | `true/false`                            | false   |
| playModeState | string | PlayMode 状态枚举   | `edit/play/pause`                       | "edit"  |
| targetWindow  | string | 鼠标事件目标窗口        | `scene/game/hierarchy/inspector/custom` | "scene" |
| mouseButton   | string | 鼠标按键            | `left/middle/right`                     | "left"  |
| mouseAction   | string | 鼠标动作            | `down/move/drag/up/click`               | "click" |
| mouseX        | float  | 窗口局部坐标 X        | `[0, +∞)`                               | 0       |
| mouseY        | float  | 窗口局部坐标 Y        | `[0, +∞)`                               | 0       |


## 3. CRUD 操作


| 操作               | 入口             | 禁用条件                   | Command类名                   | Undo语义            |
| ---------------- | -------------- | ---------------------- | --------------------------- | ----------------- |
| 创建 PlayMode 切换请求 | `playmode.set` | 编译中 `isCompiling=true` | SetPlayModeCmd              | 不进 CommandHistory |
| 更新 PlayMode 状态   | Unity 状态回调     | 无活动请求                  | UpdatePlayModeStateCmd      | 不进 CommandHistory |
| 创建鼠标事件请求         | `mouse.event`  | 目标窗口不存在                | CreateMouseEventCmd         | 不进 CommandHistory |
| 更新鼠标执行结果         | 事件执行完成后        | 请求不存在                  | CompleteMouseEventCmd       | 不进 CommandHistory |
| 删除过期输入记录         | 保留数量超限时        | 无记录                    | RemoveExpiredInputRecordCmd | 不进 CommandHistory |


## 4. 交互规格

- 触发事件：
  - 收到 `playmode.set` 或 `mouse.event`。
- 状态变化：
  - PlayMode：`edit <-> play`。
  - 鼠标：`idle -> down -> move/drag -> up`。
- 数据提交时机：
  - PlayMode 切换完成回调后提交结果。
  - 鼠标每次动作执行后立即回包。
- 取消/回退：
  - 鼠标 `down` 后若执行异常，必须补发一次 `up` 进行状态恢复。

## 5. 视觉规格

不涉及。

## 6. 校验规则


| 规则                             | 检查时机  | 级别      | 提示文案                      |
| ------------------------------ | ----- | ------- | ------------------------- |
| `playmode.set` 仅允许 `play/stop` | 命令解析时 | Error   | `非法 PlayMode 动作：{action}` |
| `mouse.event` 必须包含坐标           | 鼠标执行前 | Error   | `鼠标坐标缺失`                  |
| `button` 仅允许左中右                | 鼠标执行前 | Error   | `非法鼠标按键：{button}`         |
| 目标窗口失焦时先聚焦再注入                  | 鼠标执行前 | Warning | `目标窗口未聚焦，已自动聚焦`           |


## 7. 跨模块联动


| 模块                  | 方向   | 说明                         |
| ------------------- | ---- | -------------------------- |
| M02 UnityBridge通信模块 | 被动接收 | 接收命令并返回执行结果                |
| M01 编排服务模块          | 主动通知 | 推送 PlayMode 变化和输入执行状态      |
| M04 自动修复循环模块        | 被动接收 | 在修复完成后触发 PlayMode 冒烟操作（可选） |


## 8. 技术实现要点

- 关键类与职责：
  - `PlayModeControlService`：统一处理运行/停止。
  - `EditorMouseInputService`：窗口查找、坐标映射、事件注入。
  - `InputRecoveryGuard`：异常时补偿 `MouseUp`。
- 核心流程：
  - PlayMode：`set -> apply -> wait callback -> report`。
  - 鼠标：`focus window -> inject event -> verify -> report`。
- 性能约束：
  - 单次鼠标事件注入耗时 `< 16ms`。
  - 连续拖拽每秒支持至少 60 个 move 事件。
  - 禁止在每次鼠标事件执行时反射扫描所有 EditorWindow 类型。

## 9. 验收标准

1. [编辑器空闲] -> [调用 `playmode.set(play)`] -> [3 秒内进入 PlayMode]。
2. [处于 PlayMode] -> [调用 `playmode.set(stop)`] -> [3 秒内回到 EditMode]。
3. [Scene 窗口可见] -> [发送 left click] -> [返回执行成功]。
4. [发送 drag 序列] -> [执行 down->move->up] -> [每步均有成功回包]。

## 10. 边界规范

- 空数据：`mouse.event` 缺失 `targetWindow` 时使用默认 `scene`。
- 单元素：仅执行一次 `click` 时，内部必须拆分为 `down+up`。
- 上下限临界值：坐标为 `(0,0)` 与窗口右下角边界点均应可注入。
- 异常数据恢复：发生异常后必须清理鼠标按下状态，防止后续事件串扰。

## 11. 周边可选功能

- P1：键盘事件模拟，预留 `keyboard.event` 命令接口。
- P2：操作录制回放，预留 `inputSessionId` 与动作序列存储结构。


# M24-Build-Pipeline模块-需求文档

## 1. 模块职责
- **负责**：触发指定平台的 Unity Player 构建；管理构建状态并报告进度；获取当前支持的构建目标列表；支持中途取消构建。
- **不负责**：维护多版本构建流水线；执行发布到应用商店的操作；管理构建机器的资源分配。
- **输入输出**：输入为 `build.start/status/cancel/targets` 指令；输出为包含构建状态、输出路径、成功或错误详情的 JSON。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| :--- | :--- | :--- | :--- | :--- |
| buildTarget | string | 目标构建平台 | Windows/Android/iOS/WebGL | "StandaloneWindows64" |
| outputPath | string | 构建输出目录 | 有效路径 | "Builds/" |
| buildStatus | string | 当前状态 | idle/building/succeeded/failed | "idle" |
| buildStartedAt | long | 开始时间戳 | 正整数 | 0 |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command 类名 | Undo 语义 |
| :--- | :--- | :--- | :--- | :--- |
| 开始构建任务 | `build.start` | 正在构建中 | StartBuildCmd | 不进 CommandHistory |
| 获取构建进度 | `build.status` | 无 | GetBuildStatusCmd | 不进 CommandHistory |
| 取消当前构建 | `build.cancel` | 已经结束 | CancelBuildCmd | 不进 CommandHistory |
| 获取支持目标 | `build.targets` | 无 | ListBuildTargetsCmd | 不进 CommandHistory |

## 4. 交互规格
- **触发事件**：接收到 WebSocket `build.*` 消息。
- **状态变化**：Unity 编辑器执行构建流程，阻塞或后台线程运行。
- **数据提交时机**：`start` 发起请求后，`status` 定期轮询更新状态。
- **取消回退**：构建一旦取消不可逆，需手动清理输出文件夹。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| :--- | :--- | :--- | :--- |
| 平台有效性 | `start` 前 | 错误 | 不支持的构建平台 [x] |
| 目录写入权限 | `start` 前 | 错误 | 输出目录不可写 |
| 超时保护 | 运行期间 | 警告 | 构建超时，任务自动终止 |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| :--- | :--- | :--- |
| M02 UnityBridge | 被动接收 | 接收所有构建控制指令 |
| M03 编译 | 主动通知 | 构建前需触发完整的代码编译 |
| M01 编排服务 | 被动接收 | 调度构建任务并监控心跳 |

## 8. 技术实现要点
- **核心类**：`BuildController` (逻辑控制), `BuildMonitor` (状态记录), `BuildPlatformResolver` (平台解析)。
- **逻辑流**：`Build Start` -> `Check Compilers` -> `Execute BuildPlayer` -> `Watch Output` -> `Done`。
- **性能约束**：默认构建超时时长 600s；状态查询每秒不应超过 10 次；构建大包时严禁阻塞主通信链路。

## 9. 验收标准
- [前置条件] -> [操作：调用 build.targets] -> [期望结果：返回当前已安装的平台列表]。
- [前置条件] -> [操作：调用 build.start 并指定 WebGL] -> [期望结果：编辑器开始 WebGL 打包]。
- [前置条件] -> [操作：在构建中途调用 build.cancel] -> [期望结果：编辑器停止构建并返回取消成功]。
- [前置条件] -> [操作：在构建结束后调用 build.status] -> [期望结果：返回 succeeded 及最终文件路径]。

## 10. 边界规范
- **空数据**：`build.targets` 在未安装任何 SDK 时返回空。
- **单元素**：若项目只有一个有效场景，默认仅构建该场景。
- **上下限临界值**：构建路径支持相对及绝对路径。
- **异常数据恢复**：若构建因断电中断，下次启动恢复至 idle 状态。

## 11. 周边可选功能
- **P1**：支持自动上传构建结果至指定的 S3 或 R2 存储。
- **P2**：预留 `incremental_build` 增量构建选项。

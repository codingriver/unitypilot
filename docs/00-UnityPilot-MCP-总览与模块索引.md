# 00-UnityPilot-MCP-总览与模块索引

## 1. 项目名称
- 工具名称：`UnityPilot MCP`
- 目标版本：Unity 2022.3 LTS
- 通信主方案：WebSocket（跨 Windows / macOS 统一）

## 2. 项目目标
在手动或自动打开 Unity Editor 后，提供一套可被 Agent 调用的 MCP 能力，实现：
1. 触发编译、读取编译错误。
2. 代码自动修复并循环编译，直到无错误或达到上限。
3. 控制 PlayMode 运行/停止。
4. 在编辑器窗口模拟鼠标左/中/右键点击、按下、拖动、抬起。

## 3. 模块清单（需求文档索引）
| 模块ID | 模块名称 | 需求文档 |
| --- | --- | --- |
| M01 | 编排服务模块 | `docs/M01-编排服务模块-需求文档.md` |
| M02 | UnityBridge通信模块 | `docs/M02-UnityBridge通信模块-需求文档.md` |
| M03 | 编译与错误收集模块 | `docs/M03-编译与错误收集模块-需求文档.md` |
| M04 | 自动修复循环模块 | `docs/M04-自动修复循环模块-需求文档.md` |
| M05 | PlayMode与输入模拟模块 | `docs/M05-PlayMode与输入模拟模块-需求文档.md` |
| M06 | 代码补丁执行模块 | `docs/M06-代码补丁执行模块-需求文档.md` |
| **P0 新增** | | |
| M07 | Console日志读取模块 | `docs/M07-Console日志读取模块-需求文档.md` |
| M08 | GameObject操作模块 | `docs/M08-GameObject操作模块-需求文档.md` |
| M09 | Scene管理模块 | `docs/M09-Scene管理模块-需求文档.md` |
| M10 | Component操作模块 | `docs/M10-Component操作模块-需求文档.md` |
| M11 | 截图能力模块 | `docs/M11-截图能力模块-需求文档.md` |
| **P1 新增** | | |
| M12 | Asset管理模块 | `docs/M12-Asset管理模块-需求文档.md` |
| M13 | Prefab操作模块 | `docs/M13-Prefab操作模块-需求文档.md` |
| M14 | Material与Shader模块 | `docs/M14-Material与Shader模块-需求文档.md` |
| M15 | 菜单项执行模块 | `docs/M15-菜单项执行模块-需求文档.md` |
| M16 | Package管理模块 | `docs/M16-Package管理模块-需求文档.md` |
| M17 | 测试运行模块 | `docs/M17-测试运行模块-需求文档.md` |
| **P2 新增** | | |
| M18 | 脚本读写模块 | `docs/M18-脚本读写模块-需求文档.md` |
| M19 | CSharp代码执行模块 | `docs/M19-CSharp代码执行模块-需求文档.md` |
| M20 | 反射调用模块 | `docs/M20-反射调用模块-需求文档.md` |
| M21 | 批量操作模块 | `docs/M21-批量操作模块-需求文档.md` |
| M22 | Selection管理模块 | `docs/M22-Selection管理模块-需求文档.md` |
| M23 | MCP Resources模块 | `docs/M23-MCP-Resources模块-需求文档.md` |
| M24 | Build Pipeline模块 | `docs/M24-Build-Pipeline模块-需求文档.md` |
| **P3 远程调试** | | |
| M25 | 远程运行时调试模块 | `docs/M25-远程运行时调试模块-需求文档.md` |

## 4. 模块关系（调用链）
```text
Agent/MCP Tool
  -> M01 编排服务模块（Python WebSocket Server）
      -> M02 UnityBridge通信模块（Unity Editor WS Client）
          -> M03 编译与错误收集模块
          -> M05 PlayMode与输入模拟模块
          -> M07 Console日志读取模块
          -> M08 GameObject操作模块
              -> M10 Component操作模块
          -> M09 Scene管理模块
          -> M11 截图能力模块
          -> M12 Asset管理模块
              -> M13 Prefab操作模块
              -> M18 脚本读写模块 -> M03（触发编译）
          -> M14 Material与Shader模块
          -> M15 菜单项执行模块
          -> M16 Package管理模块 -> M03（触发编译）
          -> M17 测试运行模块 -> M05（PlayMode测试）
          -> M19 CSharp代码执行模块
          -> M20 反射调用模块
          -> M22 Selection管理模块
          -> M24 Build Pipeline模块 -> M03（编译前置）
      -> M04 自动修复循环模块
          -> M06 代码补丁执行模块
          -> M03 编译与错误收集模块（下一轮验证）
      -> M21 批量操作模块（封装任意工具组合）
      -> M23 MCP Resources模块（只读数据浏览，聚合 M07-M17）
      -> M25 远程运行时调试模块（UDP -> RShell Runtime on Device）
```

## 5. MVP 范围（首版必须）
### 5.1 能力范围
1. `unity.open_editor(command?)`
2. `unity.compile()`
3. `unity.compile_status()`
4. `unity.compile_errors()`
5. `unity.auto_fix_start(maxIterations=20)`
6. `unity.playmode_start()` / `unity.playmode_stop()`
7. `unity.mouse_event(action, button, x, y, targetWindow)`

### 5.2 MVP 通过标准
1. 能连通 Unity：握手成功、心跳稳定、断线可重连。
2. 编译通路可用：触发编译、拿到结构化错误（file/line/message）。
3. 自动修复可闭环：至少一个真实场景能从有错误到编译通过。
4. PlayMode 可控：30 秒内完成启动或停止。
5. 鼠标可控：Scene 或 Game 窗口可执行 click/down/drag/up。

## 6. 非 MVP（后续阶段）
### 6.1 P0 — 高价值核心扩展
- Console 日志读取（M07）：AI 调试的"眼睛"。
- GameObject CRUD（M08）：场景搭建基础能力。
- Scene 管理（M09）：场景级操作。
- Component 操作（M10）：组件增删改查。
- 截图能力（M11）：让 AI "看到" Unity 编辑器。

### 6.2 P1 — 开发效率提升
- Asset 管理（M12）：项目资源文件管理。
- Prefab 操作（M13）：Prefab 生命周期管理。
- Material 与 Shader（M14）：材质创建与修改。
- 菜单项执行（M15）：万能后门，覆盖大量编辑器操作。
- Package 管理（M16）：Unity Package Manager 操作。
- 测试运行（M17）：EditMode/PlayMode 单元测试。

### 6.3 P2 — 高级差异化
- 脚本读写（M18）：通过 AssetDatabase 管理 C# 脚本。
- CSharp 代码执行（M19）：Roslyn 沙箱执行任意代码片段。
- 反射调用（M20）：运行时反射调用任意方法。
- 批量操作（M21）：原子化批量工具调用。
- Selection 管理（M22）：编辑器选中项管理。
- MCP Resources（M23）：只读 URI 数据浏览。
- Build Pipeline（M24）：跨平台构建触发与管理。

## 7. 实施顺序建议
1. **Phase 1：M01 + M02**
   - 建立稳定 WS 通道（hello/heartbeat/result/error/event）。
2. **Phase 2：M03**
   - 编译状态和结构化错误输出。
3. **Phase 3：M06 + M04**
   - 文件补丁与自动修复循环打通。
4. **Phase 4：M05**
   - PlayMode 与鼠标事件模拟接入。
5. **Phase 5：M07 + M15**（P0 快速收益）
   - Console 日志读取 + 菜单项执行（投入少、价值高）。
6. **Phase 6：M11**（P0 差异化）
   - 截图能力，让 AI 获得视觉反馈。
7. **Phase 7：M08 + M09 + M10**（P0 场景操作）
   - GameObject / Scene / Component 三件套。
8. **Phase 8：M12 + M13 + M14**（P1 资源管理）
   - Asset / Prefab / Material 资源操作。
9. **Phase 9：M16 + M17 + M18**（P1 工程能力）
   - Package 管理 + 测试运行 + 脚本读写。
10. **Phase 10：M19 - M24**（P2 高级功能）
    - 代码执行、反射、批量操作、Selection、Resources、Build。
11. **Phase 11：M25**（P3 远程调试）
    - 远程运行时调试桥接（需设备已集成 RShell 运行时）。

## 8. 统一约束
1. 协议消息统一包含：`id`、`type`、`name`、`payload`、`timestamp`。
2. 所有命令必须有超时处理，超时返回标准错误对象。
3. 所有模块错误文案必须可定位（至少包含 `commandId` 或 `requestId`）。
4. 代码修复循环必须有上限，禁止无限循环。
5. 首版仅支持本机 `127.0.0.1` 通信。

## 9. 验收入口建议
- 联调脚本顺序：
  1. `ping` / `session.hello`
  2. `compile` -> `compile_status` -> `compile_errors`
  3. `auto_fix_start`
  4. `playmode_start` / `playmode_stop`
  5. `mouse_event(click/down/drag/up)`

- 验收结论输出：
  - 成功：记录每个命令耗时与结果。
  - 失败：记录错误码、错误文案、请求参数、会话状态。

## 10. 版本记录
- v1.0（2026-04-02）：创建总览文档，建立模块索引、MVP 范围和实施顺序。
- v2.0（2026-04-02）：新增 M07-M24 共 18 个扩展模块需求文档，更新模块清单、调用链与实施顺序。
- v3.0（2026-04-03）：新增 M25 远程运行时调试模块（基于 RShell 协议），更新模块清单、调用链与实施顺序。
# UnityPilot MCP 增强功能验收测试用例

> 使用 MCP 工具逐项执行以下测试。每个测试标注预期结果和判定标准。

---

## 验收流程约定（写盘与同步）

以下三条为**整次验收的固定收尾/前置步骤**，与具体用例编号无关：

1. **批量改脚本原则（Cursor / IDE 直接改工程内文件时）**  
   不要在每保存一个文件后就调 MCP。应：**等本轮所有脚本的新建、修改全部完成并已保存到磁盘之后**，再**强制调用一次**（整批一次即可）：
   - `unity_sync_after_disk_write(delayS=2, triggerCompile=true)`，或
   - `unity_sync_after_disk_write(delayS=2, triggerCompile=false)` 再按需 `unity_compile()`。  
   然后再调用 `unity_compile_wait(timeoutS=120)` 直到 `status=ready`（若上一步已 `triggerCompile=true` 且编译已结束，仍建议 `compile_wait` 确认稳定）。  
   未走 `script.update` 等 Bridge 写文件路径时，**依赖编译结果的后续 MCP 步骤**必须放在上述整批同步之后。

2. **验收会话边界**（或开始下一批依赖编译的用例前）：同样采用「本批文件改动全部结束 → **一次** `unity_sync_after_disk_write` → `unity_compile_wait`」；也可用 `unity_ensure_ready` 作为合并检查。

3. **自动修复循环（AutoFix）**在每次补丁成功落盘后，已由服务端自动执行：等待 **2 秒** → `asset.refresh` → **强制编译**（`compile.request`），无需手工重复；若同步失败会在日志中告警，下一轮仍会以 `compile.request` 开头。

**工具说明：** `unity_sync_after_disk_write` 默认 `delayS=2` 用于缓解操作系统/磁盘写延迟；`triggerCompile=false` 时仅刷新资源数据库；需要脚本重编译时设 `triggerCompile=true`。与「每文件一调」相比，**按文件批结束后统一调用**可减少重复编译并更符合磁盘落盘时序。

### T-SYNC-01: 写盘后同步 + 等待编译（批处理）
```
步骤:
1. 在 IDE 中完成本轮所有 Assets 下脚本的新建/修改，并全部保存（不要每存一个文件就执行下面两步）
2. 调用 unity_sync_after_disk_write(delayS=2, triggerCompile=true)
3. 调用 unity_compile_wait(timeoutS=120)
```
**预期:** 步骤2 `ok=true`，payload 含 `refreshed=true`；若编译成功含 `compiled=true` 与 compile 摘要；步骤3 `status=ready`
**判定:** 无需切换 Unity 焦点即可在步骤2–3内完成导入与编译；且步骤2–3为**本轮仅此一次**的强制同步

---

## P2: unity_csharp_execute 引用修复

### T-P2-01: 基础 return 语句
```
调用: unity_csharp_execute(code="return (1+2).ToString();")
```
**预期:** `ok=true`, `status="completed"`, `result="3"`
**判定:** 不再出现 CS0012 / netstandard 未引用错误

### T-P2-02: 使用 System.Linq
```
调用: unity_csharp_execute(code="var list = new System.Collections.Generic.List<int>{1,2,3}; return list.Where(x=>x>1).Count().ToString();")
```
**预期:** `ok=true`, `result="2"`
**判定:** System.Core / System.Linq 引用正常

### T-P2-03: 使用 UnityEngine API
```
调用: unity_csharp_execute(code="return UnityEngine.Application.unityVersion;")
```
**预期:** `ok=true`, `result` 包含 Unity 版本号字符串

### T-P2-04: 使用 UnityEditor API
```
调用: unity_csharp_execute(code="return UnityEditor.EditorApplication.isPlaying.ToString();")
```
**预期:** `ok=true`, `result="False"` (在 Edit 模式下)

### T-P2-05: 安全沙箱仍有效
```
调用: unity_csharp_execute(code="System.Diagnostics.Process.Start(\"notepad\"); return \"done\";")
```
**预期:** `ok=false`, 错误包含 `SECURITY_VIOLATION`

---

## P3: unity_console_get_logs 增强

### T-P3-01: 有日志时能返回内容
```
步骤:
1. 调用 unity_csharp_execute(code="UnityEngine.Debug.Log(\"MCP_TEST_LOG_12345\"); return \"ok\";")
2. 等待 1 秒
3. 调用 unity_console_get_logs(count=10)
```
**预期:** `ok=true`, `logs` 数组非空, 至少一条 message 包含 `MCP_TEST_LOG_12345`

### T-P3-02: 按类型过滤
```
步骤:
1. 调用 unity_csharp_execute(code="UnityEngine.Debug.LogWarning(\"MCP_WARN_TEST\"); return \"ok\";")
2. 调用 unity_console_get_logs(logType="Warning", count=10)
```
**预期:** 返回的 logs 中 logType 均为 "Warning"

### T-P3-03: 清空后再读取
```
步骤:
1. 调用 unity_console_clear()
2. 调用 unity_console_get_logs(count=10)
```
**预期:** 清空后通过 ring buffer 读取的日志为清空前（ring buffer 不受 console clear 影响），
通过 reflection 读取的日志为空。total >= 0，不报错

---

## P1: 窗口定位增强

### T-P1-01: 列出所有窗口
```
调用: unity_editor_windows_list()
```
**预期:** `ok=true`, `windows` 数组包含多个条目，每个有 `instanceId`, `typeName`, `fullTypeName`, `title`, `posX`, `posY`, `width`, `height`, `hasUIToolkit` 字段

### T-P1-02: 按类型过滤
```
调用: unity_editor_windows_list(typeFilter="Inspector")
```
**预期:** 返回的 windows 中 typeName 或 fullTypeName 包含 "Inspector"

### T-P1-03: 按标题过滤
```
调用: unity_editor_windows_list(titleFilter="Scene")
```
**预期:** 返回的 windows 标题中包含 "Scene"

### T-P1-04: 通过窗口标题定位 — mouse_event
```
步骤:
1. 调用 unity_editor_windows_list() 获取任意窗口标题（如 "Inspector"）
2. 调用 unity_mouse_event(action="click", button="left", x=100, y=50, targetWindow="Inspector")
```
**预期:** `ok=true`, 不出现 `WINDOW_NOT_AVAILABLE`

### T-P1-05: 通过 instanceId 定位
```
步骤:
1. 调用 unity_editor_windows_list() 获取某窗口 instanceId（假设为 12345）
2. 调用 unity_mouse_event(action="click", button="left", x=50, y=50, targetWindow="12345")
```
**预期:** `ok=true`

### T-P1-06: 通过完全限定类型名定位
```
步骤:
1. 调用 unity_editor_windows_list() 获取某窗口 fullTypeName
2. 用该 fullTypeName 调用 unity_uitoolkit_dump(targetWindow=<fullTypeName>)
```
**预期:** `ok=true`（如果窗口有 UIToolkit 内容）

### T-P1-07: UIToolkit 窗口自动路由鼠标事件
```
步骤:
1. 调用 unity_editor_windows_list() 找到 hasUIToolkit=true 的窗口
2. 调用 unity_mouse_event(action="click", button="left", x=100, y=100, targetWindow=<该窗口typeName>)
```
**预期:** `ok=true`, state 包含 `:uitoolkit` 后缀（表示走了 UIToolkit 合成路径）

---

## P5: 键盘事件 UIToolkit 兼容

### T-P5-01: UIToolkit 窗口键盘事件
```
步骤:
1. 调用 unity_editor_windows_list() 找到 hasUIToolkit=true 的窗口
2. 调用 unity_keyboard_event(action="keypress", targetWindow=<窗口名>, keyCode="Space")
```
**预期:** `ok=true`, state 包含 `:uitoolkit` 后缀

### T-P5-02: IMGUI 窗口键盘事件（回退路径）
```
调用: unity_keyboard_event(action="keypress", targetWindow="game", keyCode="Space")
```
**预期:** `ok=true`, state 不包含 `:uitoolkit`（走 IMGUI 路径）

### T-P5-03: type 动作输入文本
```
调用: unity_keyboard_event(action="type", targetWindow="inspector", text="hello")
```
**预期:** `ok=true`

---

## P4: ScrollView 滚动 API

### T-P4-01: 绝对滚动
```
步骤:
1. 调用 unity_editor_windows_list() 找到有 UIToolkit 内容的窗口
2. 调用 unity_uitoolkit_scroll(targetWindow=<窗口名>, scrollToX=0, scrollToY=200, mode="absolute")
```
**预期:** `ok=true`, `scrollOffsetY` 约等于 200

### T-P4-02: 增量滚动
```
调用: unity_uitoolkit_scroll(targetWindow="inspector", deltaX=0, deltaY=100, mode="delta")
```
**预期:** `ok=true`, 返回新的 `scrollOffsetX` 和 `scrollOffsetY`

### T-P4-03: 无 ScrollView 时优雅失败
```
步骤:
1. 选择一个确认没有 ScrollView 的窗口
2. 调用 unity_uitoolkit_scroll(targetWindow=<窗口名>, scrollToY=100)
```
**预期:** `ok=false`, state 包含 `SCROLLVIEW_NOT_FOUND`

---

## P0: 域重载连接恢复

### T-P0-01: 编译后保持连接
```
步骤:
1. 调用 unity_mcp_status() 确认 connected=true
2. 调用 unity_compile()
3. 等待返回结果（可能需要 30-60 秒，包括域重载）
4. 调用 unity_mcp_status() 再次确认
```
**预期:**
- unity_compile 返回 `ok=true` 或包含 `reconnected=true` 的成功结果
- 不再出现 `CONNECTION_LOST` 错误
- 最终 unity_mcp_status 显示 `connected=true`

### T-P0-02: 编译后能立即执行后续命令
```
步骤:
1. 调用 unity_compile()
2. 等待完成
3. 立即调用 unity_editor_state()
```
**预期:** unity_editor_state 返回 `ok=true`, `connected=true`

### T-P0-03: PlayMode 进入后保持连接
```
步骤:
1. 调用 unity_playmode_start()
2. 等待返回
3. 调用 unity_mcp_status()
4. 调用 unity_playmode_stop()
```
**预期:**
- playmode_start 返回 `ok=true`（可能含 `reconnected=true`）
- mcp_status 显示 connected=true
- playmode_stop 返回 `ok=true`

### T-P0-04: 编译错误后也能恢复
```
步骤:
1. 调用 unity_script_create(scriptPath="Assets/Editor/TempBrokenScript.cs", content="public class Broken {")
2. 调用 unity_compile()
3. 等待结果
4. 调用 unity_compile_errors()
5. 调用 unity_script_delete(scriptPath="Assets/Editor/TempBrokenScript.cs")
6. 调用 unity_compile()
```
**预期:**
- 第一次 compile 后 compile_errors 返回错误列表
- 删除脚本后第二次 compile 成功，errors=0
- 全程不出现 CONNECTION_LOST

---

## 综合测试

### T-INT-01: 完整工作流（窗口列表 → 定位 → 鼠标点击 → 键盘输入）
```
步骤:
1. unity_editor_windows_list()
2. 从结果中找到 Inspector 窗口
3. unity_mouse_event(action="click", button="left", x=200, y=100, targetWindow="inspector")
4. unity_keyboard_event(action="type", targetWindow="inspector", text="TestValue")
```
**预期:** 全部 ok=true，无 WINDOW_NOT_AVAILABLE

### T-INT-02: 编译 → 日志 → C# 执行 全链路
```
步骤:
1. unity_compile()
2. unity_console_get_logs(count=5)
3. unity_csharp_execute(code="return \"post_compile_ok\";")
```
**预期:** 编译成功（域重载后恢复），日志可读取，C# 执行返回 `"post_compile_ok"`

### T-INT-03: ScrollView + 鼠标组合
```
步骤:
1. unity_editor_windows_list(typeFilter="Inspector")
2. unity_uitoolkit_scroll(targetWindow="inspector", scrollToY=500, mode="absolute")
3. unity_mouse_event(action="click", button="left", x=200, y=300, targetWindow="inspector")
```
**预期:** 滚动成功后点击成功

---

## M26: 验收自动化工具

本节对应 P0–P3 实现的 MCP 工具与资源：`unity_compile_wait`、`unity_screenshot_editor_window`、`unity_batch_diagnostics`、`unity_verify_window`，以及资源 `unity://diagnostics/window`、`unity://console/summary`（沿用 `unity://diagnostics/unitypilot-logs-tab` 作日志区专项快照）。

### M26 前置条件（自动化脚本应先断言）

| 检查项 | 调用方式 | 通过条件 |
|--------|----------|----------|
| MCP 与 Unity 已连接 | `unity_mcp_status()` 或等价状态工具 | `connected=true`（以项目实际工具名为准） |
| 编辑器可响应 | `resource_editor_state` / `unity://editor/state` | 返回 `ok`，含 `unityVersion`、`projectPath` |
| （可选）已打开 UnityPilot 窗口 | 菜单 `UnityPilot/UnityPilot` 或 `unity_menu_execute` | 窗口存在时再跑截图/窗口诊断更有意义 |

### M26 推荐自动化流水线（单脚本顺序）

以下顺序可在一次 CI/本地脚本中串联执行，前一步失败则后续跳过或记为失败。

```
1. unity_mcp_status()                    → 确认连接
2. unity_compile_wait(timeoutS=180)    → 确保当前无编译挂起
3. unity_menu_execute("UnityPilot/UnityPilot")   → 打开窗口（若项目暴露该工具；否则人工/步骤说明）
4. unity_batch_diagnostics()           → 快照：窗口 + 控制台 + 编辑器状态
5. unity_verify_window(includeScreenshot=true)   → 一键：compileWait + 诊断 + 截图
6. （可选）读取 MCP resource unity://diagnostics/window、unity://console/summary
7. （可选）切换到「诊断日志」标签后读取 unity://diagnostics/unitypilot-logs-tab
```

### M26 自动化断言清单（脚本内可逐项 assert）

| 步骤 | 字段/路径 | 断言 |
|------|-----------|------|
| compile_wait（空闲） | `data.status` | `== "ready"` |
| compile_wait（空闲） | `data.isCompiling` | `== false` |
| batch_diagnostics | `data.windowDiagnostics` | 存在且为 object |
| batch_diagnostics | `data.consoleSummary.total` | `>= 0` |
| batch_diagnostics | `data.editorState.unityVersion` | 非空字符串 |
| verify_window | `data.compileWait.status` | `== "ready"` |
| verify_window（含截图） | `data.screenshot.imageData` | 非空 Base64 字符串 |
| verify_window（无截图） | `data.screenshot` | 键不存在或 `undefined` |
| resource window | `windowOpen` | 窗口打开时为 `true`；未打开可为 `false`（不强制失败） |
| resource window | `healthScore` | 布局无溢出时为 `"ok"`；有溢出为 `"fail"` |
| resource window | `codeVersion` | 非空 |
| resource console/summary | `total` | `== logCount + warningCount + errorCount + assertCount`（与实现一致时） |

说明：`consoleSummary` 各计数字段若与 `total` 求和存在实现差异，自动化可只断言 `total >= 0` 与各分项 `>= 0`。

### T-M26-01: unity_compile_wait — 空闲时立即返回
```
调用: unity_compile_wait(timeoutS=10, pollIntervalS=0.5)
```
**预期:** `ok=true`, `status="ready"`, `isCompiling=false`, `pollCount=1`
**判定:** 编辑器空闲时首次轮询即通过

### T-M26-02: unity_compile_wait — 编译中等待
```
步骤:
1. 触发编译（如修改脚本后保存）
2. 立即调用 unity_compile_wait(timeoutS=120, pollIntervalS=1.0)
```
**预期:** `status="ready"`, `pollCount > 1`, `elapsedS > 0`
**判定:** 编译结束后自动返回

### T-M26-03: unity_screenshot_editor_window
```
调用: unity_screenshot_editor_window(windowTitle="UnityPilot")
```
**预期:** `ok=true`, `imageData` 非空（Base64 PNG），`format="png"`
**判定:** Windows 平台能截取到编辑器窗口像素

### T-M26-04: unity_screenshot_editor_window — 窗口不存在
```
调用: unity_screenshot_editor_window(windowTitle="不存在的窗口")
```
**预期:** `ok=false`, 错误码 `WINDOW_NOT_FOUND`

### T-M26-05: unity_batch_diagnostics
```
调用: unity_batch_diagnostics()
```
**预期:** `ok=true`, 返回包含三个子对象:
- `windowDiagnostics`: 含 `windowOpen`, `healthScore`, `codeVersion`, `sections`
- `consoleSummary`: 含 `total`, `logCount`, `warningCount`, `errorCount`
- `editorState`: 含 `isCompiling`, `unityVersion`

### T-M26-06: unity_verify_window — 全自动验收
```
调用: unity_verify_window(windowTitle="UnityPilot", includeScreenshot=true)
```
**预期:** `ok=true`, 返回包含:
- `compileWait.status="ready"`
- `windowDiagnostics.healthScore="ok"`
- `consoleSummary.errorCount >= 0`
- `screenshot.imageData` 非空（Base64 PNG）

### T-M26-07: unity_verify_window — 无截图模式
```
调用: unity_verify_window(windowTitle="UnityPilot", includeScreenshot=false)
```
**预期:** `ok=true`, 返回不含 `screenshot` 字段

### T-M26-08: Resource — unity://diagnostics/window
```
读取: unity://diagnostics/window
```
**预期:** 返回 JSON 含 `windowOpen`, `windowWidth`, `windowHeight`, `healthScore`,
`codeVersion`, `domainReloadEpoch`, `isCompiling`, `sections[]`

### T-M26-09: Resource — unity://console/summary
```
读取: unity://console/summary
```
**预期:** 返回 JSON 含 `total`, `logCount`, `warningCount`, `errorCount`, `assertCount`

### T-M26-10: 标签页持久化
```
步骤:
1. 在 UnityPilot 窗口切换到「诊断日志」标签
2. 关闭窗口
3. 重新打开窗口（菜单 UnityPilot/UnityPilot）
```
**预期:** 窗口重新打开后仍停留在「诊断日志」标签（activeTab=1）

### T-M26-11: Domain Reload 纪元
```
步骤:
1. 调用 unity_batch_diagnostics() 记录 domainReloadEpoch 值
2. 触发编译（域重载）
3. 调用 unity_compile_wait() 等待编译完成
4. 再次调用 unity_batch_diagnostics()
```
**预期:** 第二次的 domainReloadEpoch > 第一次的值（域重载后自动更新）

### T-M26-12: 流水线串联 — status → compile_wait → batch → verify
```
步骤（自动化脚本顺序执行，中间 sleep 0 或极短）:
1. unity_mcp_status() 或通过 facade 确认会话已连接
2. unity_compile_wait(timeoutS=60, pollIntervalS=0.5)
3. unity_batch_diagnostics()
4. unity_verify_window(windowTitle="UnityPilot", includeScreenshot=true)
```
**预期:**
- 步骤 2：`ok=true`, `status="ready"`
- 步骤 3：`ok=true`，且 `windowDiagnostics`、`consoleSummary`、`editorState` 三键均存在
- 步骤 4：`ok=true`，且 `compileWait`、`windowDiagnostics`、`consoleSummary` 存在，`screenshot.imageData` 非空
**判定:** 单次会话内无 `UNITY_NOT_CONNECTED`，无异常栈

### T-M26-13: unity_compile_wait — 超时路径（需人为拉长编译或 mock）
```
前置: 仅在可复现「长时间编译」或测试环境可注入延迟时使用；否则标为「可选/手工」。

步骤:
1. 触发一次长编译（如大量脚本重导入）
2. 调用 unity_compile_wait(timeoutS=2, pollIntervalS=0.2)
```
**预期:** `ok=true`, `status="timeout"`, `isCompiling=true`, `elapsedS` 接近 `timeoutS`
**判定:** 证明轮询在超时后退出，而非无限阻塞

### T-M26-14: Resource 与 Tool 数据一致性
```
步骤:
1. 调用 unity_batch_diagnostics()，记录 windowDiagnostics.healthScore、consoleSummary
2. 分别通过 MCP resource 读取 unity://diagnostics/window、unity://console/summary（若 Cursor/客户端支持 resource 与 tool 并行）
```
**预期:** 同一时刻（无编译、无窗口尺寸变化）下，`healthScore` 与控制台计数与 batch 内嵌数据一致或等价
**判定:** 自动化可放宽为「同一分钟内两次读取，差值在可接受范围」

### T-M26-15: unity://diagnostics/unitypilot-logs-tab — 与全窗诊断配合
```
步骤:
1. 打开 UnityPilot 窗口并切换到「诊断日志」标签
2. 读取 unity://diagnostics/unitypilot-logs-tab
3. 读取 unity://diagnostics/window
```
**预期:**
- 日志资源：`snapshotValid=true`, `horizontalBarRisk` 与布局预期一致（正常布局下为 `false`）
- 窗口资源：`sections` 中含 `logToolbar`、`logScroll` 等记录（窗口打开且曾绘制过时）
**判定:** 专项日志区与全窗 sections 可同时用于回归「横向条」类问题

### T-M26-16: 窗口未打开时的行为
```
步骤:
1. 关闭 UnityPilot 编辑器窗口（若已打开）
2. 调用 unity_batch_diagnostics() 或读取 unity://diagnostics/window
```
**预期:** `ok=true`（连接正常时），`windowDiagnostics.windowOpen=false`，`healthScore` 可为 `unknown`（实现以代码为准）
**判定:** 自动化不应因「未开窗」而判整次失败；截图类用例需单独要求先开窗

### T-M26-17: verify_window — 截图失败降级（标题错误）
```
调用: unity_verify_window(windowTitle="__不存在的标题__", includeScreenshot=true)
```
**预期:** 整包仍可能 `ok=true`（compileWait、诊断成功），`screenshot` 内含 `error` 或错误码（与 `screenshot_editor_window` 行为一致）
**判定:** 脚本应对 `screenshot` 分支检查 `imageData` 与 `error`，避免仅断言顶层 `ok`

### T-M26-18: 标签持久化 — 与 resource 交叉验证（可选）
```
步骤:
1. 切换到「诊断日志」，关闭窗口再打开（同 T-M26-10）
2. 调用 unity_batch_diagnostics()，查看 windowDiagnostics.activeTab（若实现暴露）
   或人工确认 UI 仍为诊断日志
3. 读取 unity://diagnostics/unitypilot-logs-tab，确认 snapshotValid
```
**预期:** 持久化后首次打开若在诊断日志，`snapshotValid=true` 且 `activeTab` 与 UI 一致（以实际 payload 字段为准）
**判定:** 字段名若与文档略有出入，以 `UnityPilotLogsTabResourcePayload` / `WindowDiagnosticsPayload` 为准调整断言

---

## S系列：测试协同增强工具

### T-S1-01: UIToolkit 元素值读取 — dump 包含 value/valueType
```
调用: unity_uitoolkit_dump(targetWindow="inspector", maxDepth=8)
```
**预期:** 返回的 `elements` 中，TextField 类型的元素 `value` 字段包含当前输入框文字，`valueType="string"`；
Toggle 类型 `value="True"/"False"`，`valueType="bool"`；Button 类型 `valueType="button"`，`interactable=true`
**判定:** 检查至少存在 1 个 `valueType` 非空的元素；`interactable` 对 TextField/Toggle/Button 均为 `true`

### T-S1-02: UIToolkit 元素值读取 — query 返回 value
```
调用: unity_uitoolkit_query(targetWindow="inspector", typeFilter="TextField")
```
**预期:** 返回的 `matches` 中每个元素都有 `value`（可为空字符串）和 `valueType="string"`
**判定:** `matchCount >= 1`，且所有 match 的 `valueType == "string"`

### T-S1-03: 元素焦点状态
```
步骤:
1. unity_uitoolkit_interact(targetWindow="inspector", action="focus", elementName="<某个TextField名>")
2. unity_uitoolkit_query(targetWindow="inspector", nameFilter="<同上>")
```
**预期:** 步骤2返回的元素 `isFocused=true`
**判定:** `matches[0].isFocused == true`

### T-S2-01: UIToolkit 设置 TextField 值
```
步骤:
1. unity_uitoolkit_set_value(targetWindow="inspector", elementName="<TextField名>", value="TestValue123")
2. unity_uitoolkit_query(targetWindow="inspector", nameFilter="<同上>")
```
**预期:** 步骤1返回 `ok=true, state="set:TextField:TestValue123"`；步骤2查询到 `value="TestValue123"`
**判定:** 设置前后值变更一致

### T-S2-02: UIToolkit 设置 Toggle 值
```
步骤:
1. unity_uitoolkit_query(targetWindow="<窗口>", typeFilter="Toggle") — 记录当前值
2. unity_uitoolkit_set_value(targetWindow="<窗口>", elementName="<Toggle名>", value="true")
3. unity_uitoolkit_query(targetWindow="<窗口>", nameFilter="<同上>")
```
**预期:** 步骤2 `ok=true`，步骤3 `value="True"`
**判定:** Toggle 值被成功切换

### T-S2-03: UIToolkit interact — 按名称点击 Button
```
调用: unity_uitoolkit_interact(targetWindow="<窗口>", action="click", elementName="<Button名>")
```
**预期:** `ok=true, state="clicked:<Button名>:Button"`
**判定:** 返回 ok=true 且 state 包含 "clicked"

### T-S2-04: UIToolkit setValue — 不支持的类型返回错误
```
调用: unity_uitoolkit_set_value(targetWindow="<窗口>", elementName="<Label名>", value="abc")
```
**预期:** `ok=false, state="UNSUPPORTED_ELEMENT_TYPE:Label"`
**判定:** 不可写的元素类型返回明确错误码

### T-S3-01: compile_wait 跨域重载存活
```
步骤:
1. 修改一个 C# 脚本（触发编译和域重载）
2. 立即调用 unity_compile_wait(timeoutS=120, pollIntervalS=1)
```
**预期:** 即使期间 Unity 断连并重连，`compile_wait` 不会崩溃，最终返回 `status="ready"` 且 `reconnectedDuringWait=true`
**判定:** status=="ready"，无异常

### T-S3-02: compile_wait 超时
```
调用: unity_compile_wait(timeoutS=2, pollIntervalS=0.5)
（在编译中调用，且编译耗时 >2 秒）
```
**预期:** 返回 `status="timeout", isCompiling=true`
**判定:** 超时不崩溃，正确报状态

### T-S4-01: mouse_event 按 elementName 自动坐标
```
步骤:
1. unity_uitoolkit_dump(targetWindow="<窗口>") — 找到一个有名称的可点击元素
2. unity_mouse_event(action="click", button="left", targetWindow="<窗口>", elementName="<元素名>")
```
**预期:** `ok=true`，鼠标点击自动命中该元素中心坐标
**判定:** ok=true 且无 WINDOW_NOT_AVAILABLE

### T-S4-02: mouse_event elementName 不存在时降级
```
调用: unity_mouse_event(action="click", button="left", targetWindow="<窗口>", elementName="__不存在__")
```
**预期:** `ok=true`（降级到 x=0,y=0 坐标发送事件），或行为合理
**判定:** 不崩溃

### T-S5-01: wait_condition — 等待元素出现
```
步骤:
1. 确保某个窗口已打开且有已知元素
2. unity_wait_condition(targetWindow="<窗口>", conditionType="element_exists", elementName="<已知元素>", timeoutS=5)
```
**预期:** `met=true`，`pollCount` 较小（元素已存在，首次即命中）
**判定:** met==true

### T-S5-02: wait_condition — 元素不存在时超时
```
调用: unity_wait_condition(targetWindow="<窗口>", conditionType="element_exists", elementName="__绝对不存在__", timeoutS=3)
```
**预期:** `met=false`，`elapsedS` 接近 3
**判定:** 超时返回 met=false，不崩溃

### T-S5-03: wait_condition — 等待值匹配
```
步骤:
1. unity_uitoolkit_set_value(targetWindow="<窗口>", elementName="<TextField>", value="WaitForMe")
2. unity_wait_condition(targetWindow="<窗口>", conditionType="element_value", elementName="<TextField>", valueEquals="WaitForMe", timeoutS=5)
```
**预期:** `met=true`（值已设置，首次即命中）
**判定:** met==true 且 matchedElement.value=="WaitForMe"

### T-S5-04: wait_condition — 等待元素消失
```
调用: unity_wait_condition(targetWindow="<窗口>", conditionType="element_not_exists", elementName="__绝对不存在__", timeoutS=3)
```
**预期:** `met=true`（元素本就不存在）
**判定:** met==true

### T-S6-01: screenshot_editor_window 使用 FindTargetWindow
```
步骤:
1. unity_editor_windows_list() — 获取窗口列表，记录一个窗口的 typeName
2. unity_screenshot_editor_window(windowTitle="<typeName>")
```
**预期:** 返回 `imageData` Base64（现在支持按类型名/别名等多策略匹配）
**判定:** imageData 非空

### T-S6-02: screenshot_editor_window 别名
```
调用: unity_screenshot_editor_window(windowTitle="inspector")
```
**预期:** 成功截取 Inspector 窗口截图
**判定:** 返回 imageData 非空

### T-S7-01: ensure_ready — 正常环境
```
调用: unity_ensure_ready(timeoutS=30)
```
**预期:** `ready=true, connected=true, compileStatus="ready", inEditMode=true`
**判定:** 所有子项均为正常状态

### T-S7-02: ensure_ready — 编译中
```
步骤:
1. 修改脚本触发编译
2. 立即调用 unity_ensure_ready(timeoutS=120)
```
**预期:** 等待编译完成后返回 `ready=true`
**判定:** ready==true，compileStatus=="ready"

### T-S8-01: task_execute — 正常执行
```
调用: unity_task_execute(taskName="test_ping", toolName="resource_editor_state", timeoutS=10)
```
**预期:** `status="completed", attempt=1, result` 包含 editorState 数据
**判定:** status=="completed"

### T-S8-02: task_execute — 超时重试
```
调用: unity_task_execute(taskName="test_timeout", toolName="compile_wait", toolArgs={"timeout_s": 1, "poll_interval_s": 0.1}, timeoutS=2, retryCount=1, maxTotalS=10)
（在非编译状态执行，compile_wait 会很快返回）
```
**预期:** `status="completed"`（compile_wait 很快返回 ready）
**判定:** status=="completed"，events 列表为空或只有成功记录

### T-S8-03: task_execute — 总超时跳过
```
调用: unity_task_execute(taskName="test_skip", toolName="wait_condition",
    toolArgs={"target_window": "inspector", "condition_type": "element_exists", "element_name": "__不存在__", "timeout_s": 5},
    timeoutS=6, maxTotalS=8, retryCount=1)
```
**预期:** `status="skipped"`（wait_condition 两次均因元素不存在而返回 met=false → tool error → 跳过）
**判定:** status=="skipped"，events 包含 retry 记录

### T-INTEGRATED-01: 完整自动化流水线模拟
```
步骤:
1. unity_ensure_ready(timeoutS=60) — 预检
2. unity_editor_windows_list() — 列出窗口
3. unity_uitoolkit_dump(targetWindow="inspector") — 查看 Inspector 元素树
4. unity_uitoolkit_query(targetWindow="inspector", typeFilter="TextField") — 找 TextField
5. unity_uitoolkit_set_value(targetWindow="inspector", elementName="<找到的名>", value="PipelineTest")
6. unity_wait_condition(targetWindow="inspector", conditionType="element_value", elementName="<同上>", valueEquals="PipelineTest", timeoutS=5)
7. unity_screenshot_editor_window(windowTitle="inspector")
```
**预期:** 每一步都成功，最终截图包含修改后的值
**判定:** 全链路 ok=true / met=true，截图 imageData 非空

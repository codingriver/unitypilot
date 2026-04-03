# 02-MCP工具接口定义

## 1. 文档目标
定义 `UnityPilot MCP` 对 Agent 暴露的工具接口，约束入参、出参、错误码和调用时序。

## 2. 通用约束
1. 所有工具返回统一结构：

```json
{
  "ok": true,
  "data": {},
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000000000
}
```

2. 失败时：

```json
{
  "ok": false,
  "data": null,
  "error": {
    "code": "COMMAND_TIMEOUT",
    "message": "命令超时",
    "detail": {}
  },
  "requestId": "<uuid>",
  "timestamp": 1710000000000
}
```

3. `requestId` 由 Orchestrator 生成，必须可追踪到 WS 消息 `id`。

## 3. 工具清单
| 工具名 | 作用 | 是否 MVP |
| --- | --- | --- |
| unity.open_editor | 打开 Unity Editor（可选） | 是 |
| unity.compile | 触发编译 | 是 |
| unity.compile_status | 查询编译状态 | 是 |
| unity.compile_errors | 读取结构化编译错误 | 是 |
| unity.auto_fix_start | 启动自动修复循环 | 是 |
| unity.auto_fix_stop | 停止自动修复循环 | 是 |
| unity.playmode_start | 进入 PlayMode | 是 |
| unity.playmode_stop | 退出 PlayMode | 是 |
| unity.mouse_event | 执行鼠标事件 | 是 |
| unity.editor_state | 查询编辑器状态 | 否（P1） |

## 4. 接口定义
## 4.1 unity.open_editor
### 请求
```json
{
  "command": "<string，可选，用户提供启动命令>",
  "waitForConnectMs": 60000
}
```

### 规则
- `waitForConnectMs` 合法范围 `[5000, 180000]`，默认 `60000`。
- `command` 为空时仅等待现有 Unity 连接，不主动启动。

### 响应
```json
{
  "ok": true,
  "data": {
    "started": true,
    "connected": true,
    "sessionId": "<uuid>"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000000000
}
```

## 4.2 unity.compile
### 请求
```json
{}
```

### 规则
- 当 Unity 未连接时返回 `UNITY_NOT_CONNECTED`。
- 当编译进行中返回 `EDITOR_BUSY`。

### 响应
```json
{
  "ok": true,
  "data": {
    "accepted": true,
    "compileRequestId": "<uuid>"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000000000
}
```

## 4.3 unity.compile_status
### 请求
```json
{
  "compileRequestId": "<uuid，可选>"
}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "status": "idle|compiling|finished",
    "errorCount": 0,
    "warningCount": 0,
    "startedAt": 1710000001000,
    "finishedAt": 1710000003000
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000003001
}
```

## 4.4 unity.compile_errors
### 请求
```json
{
  "compileRequestId": "<uuid，可选>"
}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "errors": [
      {
        "file": "Assets/Scripts/A.cs",
        "line": 12,
        "column": 8,
        "message": "; expected",
        "severity": "error"
      }
    ],
    "total": 1
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000004000
}
```

## 4.5 unity.auto_fix_start
### 请求
```json
{
  "maxIterations": 20,
  "stopWhenNoError": true
}
```

### 规则
- `maxIterations` 范围 `[1, 50]`。
- 循环内顺序固定：`compile -> compile_errors -> patch -> compile`。

### 响应
```json
{
  "ok": true,
  "data": {
    "loopId": "<uuid>",
    "status": "running"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000005000
}
```

## 4.6 unity.auto_fix_stop
### 请求
```json
{
  "loopId": "<uuid>"
}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "loopId": "<uuid>",
    "status": "failed"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000007000
}
```

## 4.7 unity.playmode_start
### 请求
```json
{}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "state": "play"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000008000
}
```

## 4.8 unity.playmode_stop
### 请求
```json
{}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "state": "edit"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000009000
}
```

## 4.9 unity.mouse_event
### 请求
```json
{
  "targetWindow": "scene",
  "action": "click",
  "button": "left",
  "x": 320,
  "y": 240,
  "modifiers": ["ctrl"]
}
```

### 规则
- `action` 允许 `down/move/drag/up/click`。
- `button` 允许 `left/middle/right`。
- 坐标采用目标窗口局部坐标。

### 响应
```json
{
  "ok": true,
  "data": {
    "executed": true,
    "targetWindow": "scene",
    "action": "click",
    "button": "left"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000010000
}
```

## 4.10 unity.editor_state（P1）
### 请求
```json
{}
```

### 响应
```json
{
  "ok": true,
  "data": {
    "connected": true,
    "isCompiling": false,
    "playModeState": "edit",
    "activeScene": "SampleScene"
  },
  "error": null,
  "requestId": "<uuid>",
  "timestamp": 1710000011000
}
```

## 5. 错误码映射
| 错误码 | HTTP语义映射 | 说明 |
| --- | --- | --- |
| UNITY_NOT_CONNECTED | 503 | Unity 未连接 |
| EDITOR_BUSY | 409 | 当前状态不允许执行 |
| COMMAND_TIMEOUT | 504 | WS 命令等待超时 |
| INVALID_PAYLOAD | 400 | 参数缺失或类型非法 |
| COMMAND_NOT_FOUND | 404 | 未定义工具或命令 |
| INTERNAL_ERROR | 500 | 内部异常 |

## 6. 推荐调用时序（MVP）
1. `unity.open_editor`
2. `unity.compile`
3. `unity.compile_status`（轮询）
4. `unity.compile_errors`
5. 若错误>0：`unity.auto_fix_start`
6. 修复完成后：`unity.playmode_start` -> `unity.playmode_stop`
7. 需要交互验证时：`unity.mouse_event`

## 7. 兼容性说明
- 版本：MVP 仅保证 Unity 2022.3。
- 平台：Windows/macOS 一致协议，差异由 UnityBridge 内部适配。
- 所有新增字段必须保持向后兼容（仅新增可选字段）。
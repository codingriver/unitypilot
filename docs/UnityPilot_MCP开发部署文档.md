# UnityPilot MCP — 开发部署文档

## 目录

> 约定：本文所有命令示例默认在 `unitypilot_mcp/` 目录执行。

1. [架构概述](#1-架构概述)
2. [开发模式运行](#2-开发模式运行)
3. [修改 Python 文件后的处理](#3-修改-python-文件后的处理)
4. [重启 MCP 服务器](#4-重启-mcp-服务器)
5. [各 AI 工具的重连操作](#5-各-ai-工具的重连操作)
6. [一键脚本说明](#6-一键脚本说明)
7. [VSCode Tasks 说明](#7-vscode-tasks-说明)
8. [常见开发场景速查](#8-常见开发场景速查)
9. [常见问题](#9-常见问题)

---

## 1. 架构概述

UnityPilot MCP 由**两个服务**组成，运行在同一个 Python 进程中：

```text
┌─────────────────────────────────────────────────────┐
│              Python 进程（unitypilot-mcp）           │
│                                                     │
│  ┌───────────────────┐  ┌──────────────────────┐   │
│  │  MCP stdio 服务   │  │  WS Orchestrator     │   │
│  │  （FastMCP）      │  │  ws://127.0.0.1:8765  │   │
│  │  JSON-RPC/stdio   │  │  WebSocket 服务端     │   │
│  └─────────┬─────────┘  └──────────┬───────────┘   │
│            │                       │               │
└────────────┼───────────────────────┼───────────────┘
             │                       │
      AI 工具调用                 Unity Editor
   （Claude/Cursor/VSCode）       （C# 客户端）
```

**关键机制：**

- **MCP stdio 服务**：由 AI 工具（Claude Code / Cursor / VSCode）在每次会话时**自动启动**，通过 stdin/stdout 通信，无需手动管理。
- **WS 服务器**：作为 MCP 服务的子任务启动，Unity Editor 在打开时自动连接。
- **进程生命周期**：AI 工具启动会话 → 启动 Python 进程 → Python 进程内 WS 服务器启动 → Unity 连接。
- **editable install**（`pip install -e ..`）：Python 直接读取源码目录，**修改 `.py` 文件无需重新安装**，但需要**重启进程**才能生效。

---

## 2. 开发模式运行

开发期间有两种运行方式，根据场景选择：

### 方式 A：仅启动 WS 服务器（推荐用于开发调试）

```bash
# Windows
python -m unitypilot_mcp.main

# macOS / Linux
python3 -m unitypilot_mcp.main
```

输出：
```text
INFO     server listening on 127.0.0.1:8765
```

**特点：**
- 终端中可直接看到 WS 服务器日志
- MCP 协议部分仍由 AI 工具自动启动（指向源码目录）
- 按 `Ctrl+C` 即可停止，修改代码后重新运行

### 方式 B：启动完整 MCP+WS 进程

```bash
# Windows
python run_unitypilot_mcp.py

# macOS / Linux
python3 run_unitypilot_mcp.py
```

**特点：**
- 与 AI 工具实际启动方式完全相同
- stdin 处于阻塞等待状态（正常现象，等待 MCP 消息输入）
- 适合验证完整链路，调试 MCP 协议本身

### 方式 C：一键启动（含依赖检查 + 冒烟测试）

```bash
# Windows
deploy/dev.bat

# macOS / Linux
chmod +x deploy/dev.sh && ./deploy/dev.sh
```

**特点：**
- 自动检查 Python 版本和依赖
- 自动杀掉旧的 8765 端口进程
- 启动后运行冒烟测试验证协议
- 给出后续操作提示

### 首次开发环境搭建

```bash
# 1. 克隆/进入项目目录
cd d:/path/to/unitypilot           # Windows
cd /Users/name/path/to/unitypilot  # macOS

# 2. 安装依赖（editable 模式，修改源码无需重装）
pip install -e ..            # Windows
pip3 install -e ..           # macOS

# 3. 验证安装
python src/unitypilot_mcp/mcp_smoke_test.py
# 期望：[OK] MCP stdio smoke test passed (11 tools)

# 4. 启动开发服务器
python -m unitypilot_mcp.main
```

---

## 3. 修改 Python 文件后的处理

### 核心原则

> **Python 是解释型语言**：进程启动时加载所有模块到内存，运行期间**不会**自动重新加载修改的文件。修改 `.py` 文件后，**必须重启进程**才能生效。

### 不同情况的处理

| 修改内容 | 是否需要重启 | 说明 |
| --- | --- | --- |
| `src/unitypilot_mcp/*.py`（任意 Python 文件） | **是** | 进程重启后立即生效 |
| `pyproject.toml`（仅改版本号/描述） | 否 | 不影响运行时 |
| `pyproject.toml`（新增/修改依赖） | **是** | 还需先 `pip install -e ..` |
| `Assets/**/*.cs`（Unity C# 文件） | 否（Unity 侧） | Unity 会自动编译 |
| `.mcp.json`（MCP 配置） | 需重启 AI 工具会话 | AI 工具读取配置时才生效 |

### 快速判断

```text
改了 .py 文件？
  └── 是 → 重启 WS 服务器（见第 4 节）
       └── AI 工具下次调用工具时自动感知新版本

改了 pyproject.toml 依赖？
  └── pip install -e ..  → 再重启服务器

改了 .mcp.json 配置？
  └── 重启 AI 工具会话（关闭再打开 Claude Code / Cursor）
```

---

## 4. 重启 MCP 服务器

### 重启机制说明

MCP 服务器由**两层**组成，重启策略不同：

| 层 | 进程 | 管理方 | 重启方式 |
| --- | --- | --- | --- |
| **MCP stdio 层** | `python run_unitypilot_mcp.py` | AI 工具自动管理 | 关闭/重开 AI 会话 |
| **WS 服务层** | 监听 `:8765` 的 Python 进程 | 开发者手动管理 | 杀进程后重启 |

**开发期最常见的场景**：修改了 Python 代码后，只需重启 **WS 服务层**（监听 8765 的进程），AI 工具会话可以保持不动，下次调用工具时会自动用新代码处理请求。

---

### 4.1 Windows 重启

#### 方法 1：一键脚本（推荐）

```bat
deploy/restart_mcp.bat
```

双击或在命令提示符中运行，脚本会自动找到并杀掉 8765 端口进程，然后重启。

#### 方法 2：命令提示符手动操作

```bat
REM 步骤 1：找到并杀掉 8765 端口进程
for /f "tokens=5" %p in ('netstat -ano ^| findstr ":8765 " ^| findstr LISTENING') do taskkill /PID %p /F

REM 步骤 2：重启服务器
python -m unitypilot_mcp.main
```

#### 方法 3：任务管理器

1. 打开任务管理器 → 详细信息
2. 找到 `python.exe`（可通过"命令行"列确认含 `unitypilot`）
3. 右键 → 结束任务
4. 重新运行 `python -m unitypilot_mcp.main`

#### 方法 4：VSCode Tasks（推荐 VSCode 用户）

`Ctrl+Shift+P` → `Tasks: Run Task` → 选择 **`UnityPilot: 重启 WS 服务器`**

---

### 4.2 macOS 重启

#### 方法 1：一键脚本（推荐）

```bash
# 首次需要赋予执行权限（只需一次）
chmod +x deploy/restart_mcp.sh

# 重启
./deploy/restart_mcp.sh

# 仅停止，不重启
./deploy/restart_mcp.sh stop
```

#### 方法 2：终端手动操作

```bash
# 步骤 1：找到并杀掉 8765 端口进程
kill -9 $(lsof -ti tcp:8765)

# 步骤 2：重启服务器
python3 -m unitypilot_mcp.main
```

#### 方法 3：如果服务器在前台终端运行

直接在终端按 `Ctrl+C`，然后：

```bash
python3 -m unitypilot_mcp.main
```

#### 方法 4：VSCode Tasks

`Cmd+Shift+P` → `Tasks: Run Task` → 选择 **`UnityPilot: 重启 WS 服务器`**

---

### 4.3 确认重启成功

重启后确认服务器已就绪：

```bash
# Windows（在新的命令提示符窗口）
netstat -ano | findstr ":8765 " | findstr LISTENING

# macOS / Linux
lsof -i tcp:8765 -sTCP:LISTEN

# 任何平台：完整冒烟测试
python src/unitypilot_mcp/mcp_smoke_test.py   # Windows
python3 src/unitypilot_mcp/mcp_smoke_test.py  # macOS
```

期望输出：
```text
[OK] MCP stdio smoke test passed (11 tools)
```

---

## 5. 各 AI 工具的重连操作

### 背景

WS 服务器重启后：

- **Unity Editor**：会自动检测断线并在约 2–6 秒内重新连接，无需手动操作。
- **AI 工具（MCP 层）**：MCP stdio 进程通常与 WS 进程分离（AI 工具每次调用工具时重新与服务器通信），一般**无需任何操作**，下次调用工具即可。

以下情况需要手动重连 AI 工具（通常是 `.mcp.json` 配置改动，或 MCP stdio 进程异常退出）：

### Claude Code

```bash
# 方法 1：重新检查连接状态
claude mcp list
# 如果显示 ✓ Connected，无需操作

# 方法 2：重启会话（清空上下文）
# 在 Claude Code 窗口输入：/reset 或关闭当前会话重新打开

# 方法 3：命令行强制重新加载
claude mcp restart unitypilot  # 如果此命令存在
```

### Cursor

1. `Ctrl+Shift+P`（Win）/ `Cmd+Shift+P`（Mac）→ 搜索 `MCP`
2. 找到 `Restart MCP Server` 或进入 **Settings → MCP**
3. 点击 `unitypilot` 旁边的刷新/重启图标

或直接关闭并重新打开 Cursor 窗口。

### VSCode Copilot

1. `Ctrl+Shift+P` / `Cmd+Shift+P` → 搜索 `MCP`
2. 选择 `GitHub Copilot: Restart MCP Server`
3. 或关闭 VSCode 重新打开项目

### OpenCode

重启 OpenCode 应用即可，MCP 服务器会在新会话时自动启动。

---

## 6. 一键脚本说明

项目中的部署脚本统一放在 `deploy/`，Windows 用 `.bat`，macOS/Linux 用 `.sh`：

| 脚本 | 平台 | 功能 |
| --- | --- | --- |
| `deploy/dev.bat` / `deploy/dev.sh` | Win / Mac | 开发模式一键启动：检查依赖 → 清理旧进程 → 启动服务器 → 冒烟测试 |
| `deploy/restart_mcp.bat` / `deploy/restart_mcp.sh` | Win / Mac | 重启 WS 服务器：杀旧进程 → 启动新进程 → 确认端口就绪 |
| `deploy/build_release.py` | 通用（Python） | 打包发布：构建 wheel → 下载依赖 → 生成配置模板 → 打 zip |

部署资源统一放在 `deploy/resources/`（例如历史发布 zip、安装素材等）。

### deploy/dev.bat / deploy/dev.sh 详细流程

```text
1. 检查 Python 版本（需 3.11+）
2. 检查 mcp / websockets 模块是否安装，若缺失自动执行 pip install -e ..
3. 配置 `PYTHONPATH=<项目根>/unitypilot_mcp/src`
4. 杀掉占用 8765 端口的旧进程
5. 后台启动 WS 服务器（python -m unitypilot_mcp.main）
6. 等待端口就绪（最长 10 秒）
7. 运行 mcp_smoke_test.py 验证协议
8. 输出操作提示
```

### deploy/restart_mcp.bat 参数

```bat
deploy/restart_mcp.bat         # 停止旧进程并重启
```

### deploy/restart_mcp.sh 参数

```bash
./deploy/restart_mcp.sh            # 停止旧进程并重启（默认）
./deploy/restart_mcp.sh stop       # 仅停止，不重启
./deploy/restart_mcp.sh restart    # 等同于默认
```

---

## 7. VSCode Tasks 说明

在 `.vscode/tasks.json` 中定义了 7 个任务，通过 `Ctrl+Shift+P` → `Tasks: Run Task` 访问：

| 任务名 | 快捷触发 | 说明 |
| --- | --- | --- |
| **开发模式启动** | Run Task | 启动 WS 服务器，输出保留在专用终端面板 |
| **停止 WS 服务器** | Run Task | 杀掉 8765 端口进程（跨平台命令） |
| **重启 WS 服务器** | Run Task | 依次执行停止 + 启动（**修改代码后用此任务**） |
| **MCP 冒烟测试** | Run Task | 验证 MCP 协议，无需 Unity / WS 服务器运行 |
| **安装/更新依赖** | Run Task | `pip install -e ..`，首次或依赖变更后运行 |
| **打包发布（当前平台）** | Run Task | 运行 `deploy/build_release.py` |
| **启动 MCP+WS（stdio 调试）** | Run Task | 完整进程调试用，stdio 阻塞属正常 |

**推荐绑定快捷键**（在 VSCode `keybindings.json` 中添加）：

```json
[
  {
    "key": "ctrl+shift+r",
    "command": "workbench.action.tasks.runTask",
    "args": "UnityPilot: 重启 WS 服务器"
  }
]
```

---

## 8. 常见开发场景速查

### 场景 1：修改了工具逻辑（`tool_facade.py` 等）

```bash
# Windows
deploy/restart_mcp.bat

# macOS
./deploy/restart_mcp.sh

# VSCode（任何平台）
Ctrl/Cmd+Shift+P → Tasks: Run Task → UnityPilot: 重启 WS 服务器
```

修改立即生效，Unity 会自动重连，AI 工具下次调用工具即用新代码。

### 场景 2：新增了依赖包

```bash
# 1. 在 pyproject.toml [project.dependencies] 中添加依赖
# 2. 安装
pip install -e ..       # Windows
pip3 install -e ..      # macOS

# 3. 重启服务器
deploy/restart_mcp.bat        # Windows
./deploy/restart_mcp.sh       # macOS
```

### 场景 3：修改了 MCP 工具定义（`mcp_stdio_server.py`）

```bash
# MCP stdio 进程由 AI 工具管理，需要 AI 工具重新启动进程
# 步骤 1：重启 WS 服务器（确保代码更新）
deploy/restart_mcp.bat / ./deploy/restart_mcp.sh

# 步骤 2：在 AI 工具中触发 MCP 重连
# Claude Code：claude mcp list  （会自动检测）
# Cursor：Settings → MCP → 刷新
# VSCode：Cmd+Shift+P → Restart MCP Server
```

### 场景 4：修改了 `.mcp.json` 配置

```bash
# 只需重启 AI 工具会话，不需要重启 Python 服务器
# Claude Code：关闭当前窗口重新打开
# Cursor：重启 Cursor
# VSCode：重载窗口（Ctrl+Shift+P → Reload Window）
```

### 场景 5：准备提测 / 发布新版本

```bash
# 1. 更新版本号
#    修改 pyproject.toml 中的 version = "x.y.z"

# 2. 打包
python deploy/build_release.py --platform win   # Windows 机器上
python3 deploy/build_release.py --platform mac  # macOS 机器上

# 3. 验证产物
ls dist/
# unitypilot-mcp-x.y.z-win64.zip
# unitypilot-mcp-x.y.z-macos.zip
```

---

## 9. 常见问题

### Q: 重启后 Unity 不自动重连

检查 Unity 控制台是否有错误。Unity 侧的重连间隔约 2–6 秒，最多等待 10 秒。若超时：

1. Unity 菜单 → **UnityPilot/UnityPilot**（状态窗口），点击"重新连接"按钮
2. 或重启 Unity Editor（会在启动时自动连接）

### Q: 停止服务器后端口仍显示占用（Windows）

TIME_WAIT 状态的 TCP 连接：

```bat
REM 确认是否仍有 LISTENING 状态（LISTENING 才影响重启）
netstat -ano | findstr ":8765"
REM 只有 LISTENING 行才需要处理，TIME_WAIT 可忽略
```

### Q: macOS 上 `lsof` 找不到进程但重启失败

```bash
# 使用 ss 或 netstat 替代
netstat -an | grep 8765
# 或直接用 fuser
fuser -k 8765/tcp
```

### Q: 修改代码后重启，AI 工具仍报旧错误

AI 工具可能缓存了上一次的工具调用结果。在 AI 工具对话框中明确说明"重新调用工具"，或重启 AI 工具会话。

### Q: `pip install -e ..` 每次都很慢

原因：依赖已安装但 pip 仍在检查更新。加 `--no-deps` 可跳过依赖检查：

```bash
pip install -e .. --no-deps -q
```

仅在新增依赖时才需要完整安装：

```bash
pip install -e .. -q
```

### Q: 两个 AI 工具同时使用，端口冲突

同一台机器同时运行两个 AI 工具会话，它们各自会尝试启动一个 MCP 服务器，都会绑定 `:8765`，第二个会失败。解决方案：

- 只让一个 AI 工具会话处于活动状态
- 或修改其中一个的端口（需同时修改 Unity 侧配置）

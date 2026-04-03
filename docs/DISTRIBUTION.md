# UnityPilot MCP — 打包、发布与安装指南

## 目录

> 约定：本文所有命令示例默认在 `unitypilot_mcp/` 目录执行。

1. [项目结构](#1-项目结构)
2. [一键打包脚本](#2-一键打包脚本)
3. [手动打包发布](#3-手动打包发布)
4. [安装方式对比](#4-安装方式对比)
5. [Claude Code 配置](#5-claude-code-配置)
6. [Cursor 配置](#6-cursor-配置)
7. [VSCode 配置](#7-vscode-配置)
8. [Unity 侧配置](#8-unity-侧配置)
9. [验证连通性](#9-验证连通性)
10. [常见问题](#10-常见问题)

---

## 1. 项目结构

```text
unitypilot_mcp/
  src/unitypilot_mcp/     # Python 源码（全部脚本与模块）
    __init__.py
    mcp_main.py           # CLI 入口（_cli 函数）
    mcp_stdio_server.py   # FastMCP 工具定义 + WS 服务器生命周期
    server.py             # WebSocket Orchestrator（监听 8765）
    tool_facade.py        # 工具实现
    dispatcher.py / ...   # 内部模块
  deploy/                 # 部署脚本与部署资源
    build_release.py      # 一键打包脚本
    dev.bat / dev.sh
    restart_mcp.bat / restart_mcp.sh
    resources/            # 部署资源（如历史发布包等）
run_unitypilot_mcp.py     # 开发期直接运行脚本（位于 unitypilot_mcp/ 目录）
pyproject.toml            # 包元数据（位于项目根，路径 `../pyproject.toml`）
```

**通信架构：**

```text
AI 工具 ──JSON-RPC/stdio──► Python MCP 服务器（unitypilot-mcp）
                                    │
                            WebSocket :8765
                                    │
                           Unity Editor（C# 客户端）
```

**为什么需要分平台打包：**

MCP 协议（JSON-RPC/stdio）对所有 AI 工具完全相同（Claude Code / Cursor / VSCode Copilot / OpenCode），服务端二进制无需区分。但 Python 二进制依赖（`pydantic-core`、`cryptography`、`pywin32`、`websockets` 等）含平台专属的 `.whl` 文件，因此需要在各平台分别打包。

---

## 2. 一键打包脚本

`deploy/build_release.py` 是 `deploy/` 目录下的打包脚本，完成以下工作：

1. 构建本包的 wheel
2. 下载全部运行时依赖 wheel（针对当前平台，共约 32 个）
3. 生成各 AI 工具的 MCP 配置文件模板
4. 生成 `install.bat`（Windows）和 `install.sh`（macOS）安装脚本
5. 将所有内容打包为一个 zip 离线安装包

### 2.1 前置要求

- Python 3.11 或以上
- 网络连接（用于下载依赖）
- `build` 工具（脚本会自动安装，也可手动执行 `pip install build`）

### 2.2 命令格式

```text
python deploy/build_release.py [--platform {win,mac,auto}]
```

### 2.3 参数说明

| 参数         | 简写 | 可选值                 | 默认值 | 说明                              |
| ------------ | ---- | ---------------------- | ------ | --------------------------------- |
| `--platform` | `-p` | `win` / `mac` / `auto` | `auto` | 目标平台，`auto` 自动检测当前系统 |

**`--platform` 详细说明：**

- `auto`（默认）：自动检测当前操作系统。Windows 上产出 `win64` 包，macOS 上产出 `macos` 包。
- `win`：强制输出 Windows 包（`win64`）。**必须在 Windows 上运行**，因为需要下载 Windows 专属二进制 wheel（`pywin32`、`cryptography-win_amd64` 等）。
- `mac`：强制输出 macOS 包（`macos`）。**必须在 macOS 上运行**，原因同上。

> 不支持跨平台交叉编译。需要出哪个平台的包，就在对应平台的机器上运行脚本。

### 2.4 使用示例

```bash
# 示例 1：当前平台打包（最常用）
python deploy/build_release.py

# 示例 2：明确指定 Windows 包（在 Windows 机器上）
python deploy/build_release.py --platform win

# 示例 3：明确指定 macOS 包（在 macOS 机器上）
python deploy/build_release.py -p mac

# 示例 4：查看帮助
python deploy/build_release.py --help
```

### 2.5 输出产物

产物统一放在 `dist/` 目录，命名格式为 `unitypilot-mcp-<版本号>-<平台>.zip`：

```text
dist/
  unitypilot-mcp-0.1.0-win64.zip    # Windows 离线包（约 17 MB）
  unitypilot-mcp-0.1.0-macos.zip    # macOS 离线包（约 13 MB）
```

### 2.6 zip 包内结构

```text
unitypilot-mcp-0.1.0-win64.zip
  wheels/
    unitypilot_mcp-0.1.0-py3-none-any.whl    # 本包
    mcp-1.26.0-py3-none-any.whl
    websockets-16.0-cp311-cp311-win_amd64.whl
    pydantic_core-2.41.5-cp311-cp311-win_amd64.whl
    cryptography-46.0.6-cp311-abi3-win_amd64.whl
    pywin32-311-cp311-cp311-win_amd64.whl
    ...（共约 33 个 wheel，含全部依赖）
  install.bat        # Windows 一键安装（双击运行）
  install.sh         # macOS/Linux 一键安装
  mcp-configs/
    claude-code.mcp.json    # Claude Code / OpenCode / Claude VSCode 扩展
    cursor.mcp.json         # Cursor
    vscode.mcp.json         # VSCode Copilot
  README.txt         # 快速上手说明
```

### 2.7 打包过程输出示例

```text
============================================================
  打包平台: win64  (版本 0.1.0)
============================================================

[1/4] 构建 wheel...
  $ python -m build --wheel --outdir ...
  wheel: unitypilot_mcp-0.1.0-py3-none-any.whl

[2/4] 下载依赖 wheel...
  $ python -m pip download ... --dest ...
  共下载 32 个依赖包

[3/4] 生成脚本和配置文件...
  配置文件: ['claude-code.mcp.json', 'cursor.mcp.json', 'vscode.mcp.json']

[4/4] 打包 zip...
  + install.bat
  + install.sh
  + mcp-configs/claude-code.mcp.json
  ...
  + wheels/unitypilot_mcp-0.1.0-py3-none-any.whl
  ...

  产物: dist/unitypilot-mcp-0.1.0-win64.zip  (16.6 MB)
```

### 2.8 版本号更新

修改 `../pyproject.toml` 中的 `version` 字段后重新运行脚本即可：

```toml
[project]
version = "0.2.0"   # 改这里
```

---

## 3. 手动打包发布

仅在需要发布到 PyPI 或单独构建 wheel 时使用此节，日常出离线包请直接用 `deploy/build_release.py`。

### 3.1 构建 wheel

```bash
pip install build
python -m build --wheel --outdir dist ..
# 产物：dist/unitypilot_mcp-0.1.0-py3-none-any.whl
```

### 3.2 发布到 PyPI

```bash
pip install twine

# 先测试
twine upload --repository testpypi dist/*

# 正式发布
twine upload dist/*
```

> 需要 PyPI 账号并生成 API Token。发布后用户可通过 `pip install unitypilot-mcp` 直接安装。

### 3.3 发布到 GitHub Releases

将 `dist/*.whl` 上传到 GitHub Release Assets，用户通过以下命令安装：

```bash
pip install https://github.com/your-org/your-repo/releases/download/v0.1.0/unitypilot_mcp-0.1.0-py3-none-any.whl
```

---

## 4. 安装方式对比

用户收到离线包后的安装方式：

| 方式 | 适用场景 | 操作 |
| --- | --- | --- |
| **离线包（推荐）** | 已收到 `deploy/build_release.py` 产出的 zip | 解压后运行 `install.bat` / `install.sh` |
| **pip + PyPI** | 已发布到 PyPI | `pip install unitypilot-mcp` |
| **uvx** | 已发布到 PyPI，追求零依赖管理 | 无需安装，直接在配置中使用 `uvx unitypilot-mcp` |
| **源码** | 开发调试 | `pip install -e ..` |

### 离线包安装步骤

**Windows：**

```bat
1. 解压 unitypilot-mcp-0.1.0-win64.zip
2. 双击 install.bat（或在命令提示符中运行）
3. 等待提示"安装完成！"
```

**macOS：**

```bash
unzip unitypilot-mcp-0.1.0-macos.zip
cd unitypilot-mcp-0.1.0-macos
chmod +x install.sh
./install.sh
```

安装完成后系统中出现 `unitypilot-mcp` 命令，可在 MCP 配置中直接引用。

### uvx 安装（PyPI 发布后）

```bash
# 安装 uv（一次性操作）
# Windows PowerShell：
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
# macOS：
curl -LsSf https://astral.sh/uv/install.sh | sh
```

安装后无需手动安装包，直接在 MCP 配置文件中使用 `uvx unitypilot-mcp` 即可。

---

## 5. Claude Code 配置

Claude Code 通过项目根目录的 `.mcp.json` 自动加载服务器。

**离线包安装后：** 将 `mcp-configs/claude-code.mcp.json` 复制到 Unity 项目根目录并重命名为 `.mcp.json`。

**手动创建 `.mcp.json`：**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

> macOS 不需要 `PYTHONUTF8`，保留无害。

**uvx 方式（PyPI 发布后）：**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "uvx",
      "args": ["unitypilot-mcp"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**本地源码方式（开发期）：**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "python",
      "args": ["d:/SkillEditor/unitypilot_mcp/run_unitypilot_mcp.py"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

> macOS：`"command": "python3"`，路径改为 `/Users/name/SkillEditor/unitypilot_mcp/run_unitypilot_mcp.py`

**验证：**

```bash
claude mcp list
# 期望：unitypilot: ... - ✓ Connected
```

---

## 6. Cursor 配置

| 配置范围         | 文件路径                       |
| ---------------- | ------------------------------ |
| 全局（所有项目） | `~/.cursor/mcp.json`           |
| 项目级           | `<项目根>/.cursor/mcp.json`    |

**离线包安装后：** 将 `mcp-configs/cursor.mcp.json` 复制到 `<项目根>/.cursor/mcp.json`（目录不存在则新建）。

**手动创建配置（格式与 Claude Code 相同）：**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**macOS 本地源码方式：**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "python3",
      "args": ["/Users/name/SkillEditor/unitypilot_mcp/run_unitypilot_mcp.py"]
    }
  }
}
```

**验证：** Cursor → Settings → MCP，`unitypilot` 显示绿色圆点即为已连接。

---

## 7. VSCode 配置

VSCode 通过 GitHub Copilot 扩展（需 1.99+）支持 MCP，配置格式与 Claude Code / Cursor **有所不同**：使用 `"servers"` 键（而非 `"mcpServers"`），且需要 `"type": "stdio"` 字段。

| 配置范围 | 文件路径 |
| --- | --- |
| 项目级 | `<项目根>/.vscode/mcp.json` |
| 全局 | `~/.vscode/settings.json`（写入 `github.copilot.chat.mcp.servers`） |

**离线包安装后：** 将 `mcp-configs/vscode.mcp.json` 复制到 `<项目根>/.vscode/mcp.json`（目录不存在则新建）。

**手动创建 `.vscode/mcp.json`：**

```json
{
  "servers": {
    "unitypilot": {
      "type": "stdio",
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**uvx 方式：**

```json
{
  "servers": {
    "unitypilot": {
      "type": "stdio",
      "command": "uvx",
      "args": ["unitypilot-mcp"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**Claude VSCode 扩展：** 配置方式与 Claude Code CLI 相同，使用 `"mcpServers"` 键，放置 `.mcp.json` 文件。

---

## 8. Unity 侧配置

### 8.1 安装插件

将以下目录复制到目标 Unity 项目：

```text
Assets/SkillEditor/Editor/UnityPilot/
  UnityPilotBootstrap.cs
  UnityPilotBridge.cs
  UnityPilotCompileService.cs
  UnityPilotProtocol.cs
  UnityPilotStatusWindow.cs
```

### 8.2 启用

Unity 菜单 → **SkillEditor → 启用 UnityPilot**（勾选）

### 8.3 连接顺序

```text
1. 启动 AI 工具 → MCP 服务器自动启动，监听 ws://127.0.0.1:8765
2. 打开 Unity → UnityPilot 插件自动连接
3. 菜单 SkillEditor → UnityPilot 状态监控 → 显示"已连接"
```

> MCP 服务器必须先于 Unity 启动。Unity 是 WebSocket 客户端，Python 是服务端。

---

## 9. 验证连通性

### 9.1 冒烟测试（无需 Unity）

```bash
cd /path/to/SkillEditor/unitypilot_mcp
python src/unitypilot_mcp/mcp_smoke_test.py

# 期望输出：
# [OK] MCP stdio smoke test passed (11 tools)
```

### 9.2 Claude Code 连接测试

```bash
claude mcp list
# 期望：unitypilot: ... - ✓ Connected
```

### 9.3 在 AI 工具中调用工具

在 Claude Code / Cursor 对话框中输入：

```text
调用 unity_editor_state 工具，告诉我当前编辑器状态
```

---

## 10. 常见问题

### Q: `install.bat` 报错"找不到 python"

Python 未加入系统 PATH。解决方案：

- 重新安装 Python，勾选 **"Add Python to PATH"**
- 或手动在命令提示符中运行：`where python` 找到路径后直接调用

### Q: Windows 上 `unitypilot-mcp` 命令找不到

pip `--user` 安装的脚本可能不在 PATH 中。解决方案：

```bash
# 找到用户 Scripts 目录
python -c "import site; print(site.getusersitepackages())"
# 将对应的 Scripts/ 目录加入 PATH
# 或在 .mcp.json 中使用完整路径：
# "command": "C:/Users/name/AppData/Roaming/Python/Python311/Scripts/unitypilot-mcp.exe"
```

### Q: macOS 上 `python` 找不到

macOS 默认无 `python` 命令，使用 `python3`。在 MCP 配置和 `install.sh` 中均已使用 `python3`。若仍报错，先安装 Python：

```bash
brew install python
```

### Q: 端口 8765 被占用

修改 `src/unitypilot_mcp/server.py` 中 `WsOrchestratorServer` 的默认端口，并同步修改 Unity 侧 `UnityPilotBridge.cs` 中的连接地址。

### Q: `claude mcp list` 显示 Failed to connect

- Python 版本低于 3.11 → 升级
- `mcp` 包未安装 → `pip install mcp>=1.26.0`
- 使用了离线包安装但 `install.bat` 未执行成功 → 检查 Python 是否在 PATH 中

### Q: Cursor 中看不到 MCP 工具

Cursor 版本需 ≥ 0.46，在 **Settings → Features → MCP** 中确认已启用 MCP 功能。

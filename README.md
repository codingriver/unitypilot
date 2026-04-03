# unitypilot

这是一个基于 Model Context Protocol (MCP) 的 Unity 编辑器连接器，允许 AI 助手（如 OpenCode, Claude Desktop, Cursor, Windsurf 等）直接控制和操作 Unity 场景、资源、组件、编译和构建管线。

## 项目概览

unitypilot-mcp 是 AI 与 Unity 之间的桥梁。它充当 MCP 服务端，通过标准输入输出 (STDIO) 与 AI 交互，并利用 WebSocket 与 Unity 编辑器中的 unitypilot 插件通信，从而实现对 Unity 的远程操控。

### 架构图

```text
+-----------+       +------------------+       +------------+       +--------------+
|           |       |                  |       |            |       |              |
| AI 工具   | <---> | unitypilot-mcp   | <---> | WebSocket  | <---> | Unity Editor |
| (OpenCode)| STDIO | (MCP Server)     | TCP   | (8765 端口) |       | (C# Plugin)  |
|           |       |                  |       |            |       |              |
+-----------+       +------------------+       +------------+       +--------------+
```

## 快速开始

1. **准备环境**：确保安装了 Python >= 3.11。
2. **安装插件**：在 Unity 项目中安装 unitypilot C# 插件并保持编辑器开启。
3. **运行服务**：
   ```bash
   python run_unitypilot_mcp.py --port 8765
   ```
4. **配置 AI**：在你的 AI 工具中添加此 MCP 服务。

## 环境要求

- **Python**: >= 3.11
- **Unity**: 已安装 unitypilot C# 插件的 Unity 编辑器
- **依赖库**: mcp >= 1.26.0, websockets >= 12.0

## 安装方法

### 方式 A：直接运行（无需安装）
直接在项目根目录下使用 Python 执行入口脚本：
```bash
python run_unitypilot_mcp.py
```

### 方式 B：开发者模式安装
在项目根目录下执行安装命令，之后可以直接调用 `unitypilot-mcp` 命令：
```bash
pip install -e .
unitypilot-mcp --port 8765
```

## AI 工具配置指南

### OpenCode
在 `opencode.json`（位于 `~/.config/opencode/opencode.json`）的 `mcp` 字段中添加：
```json
{
  "mcp": {
    "unitypilot": {
      "type": "local",
      "command": ["python", "D:/unitypilot/run_unitypilot_mcp.py", "--log-file", "D:/unitypilot/mcp.log"],
      "environment": {
        "PYTHONUTF8": "1"
      }
    }
  }
}
```

> **注意**: OpenCode 使用 `"mcp"` 作为顶层键（不是 `"mcpServers"`），命令以**数组**形式提供，环境变量的键名是 `"environment"`（不是 `"env"`）。

### Claude Desktop / Cursor / Windsurf
将仓库中的 `mcp.example.json` 复制为项目根目录的 `.mcp.json`，或使用 Cursor 时在项目下创建 `.cursor/mcp.json`（把示例中的 `<PATH_TO_UNITYPILOT_REPO>` 换成本机绝对路径）。在配置文件中添加或合并为：
```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "python",
      "args": [
        "D:/unitypilot/run_unitypilot_mcp.py",
        "--port",
        "8765",
        "--log-file",
        "D:/unitypilot/logs/mcp.log"
      ],
      "env": {
        "UNITYPILOT_LOG_LEVEL": "INFO"
      }
    }
  }
}
```

## 参数参考

所有 CLI 参数均可通过环境变量设置。CLI 参数优先级高于环境变量。

| 参数名称 | CLI 标志 | 环境变量 | 默认值 | 描述 |
|---|---|---|---|---|
| WebSocket 主机 | `--host` | `UNITYPILOT_HOST` | `127.0.0.1` | WebSocket 监听地址 |
| WebSocket 端口 | `--port` | `UNITYPILOT_PORT` | `8765` | WebSocket 监听端口 |
| 日志文件 | `--log-file` | `UNITYPILOT_LOG_FILE` | (无) | 日志输出文件路径 |
| 日志级别 | `--log-level` | `UNITYPILOT_LOG_LEVEL` | `DEBUG` | 日志级别: DEBUG/INFO/WARNING/ERROR/CRITICAL |

**端口重试逻辑**：如果端口被占用，服务会尝试重新连接 5 次，每次间隔 5 秒。如果全部失败，程序将自动退出。

## 工具列表 (共 67 个)

### 编辑器连接
- `unity_open_editor`: 检查 Unity 连接并返回会话信息。
- `unity_compile`: 触发 Unity 编译。
- `unity_compile_status`: 获取最近一次编译状态。
- `unity_compile_errors`: 获取最近一次结构化编译错误（不回退缓存）。

### 诊断
- `unity_mcp_status`: 诊断 MCP 连接、会话、超时及编译状态。

### 自动修复
- `unity_auto_fix_start`: 启动自动修复循环。
- `unity_auto_fix_stop`: 停止自动修复循环。
- `unity_auto_fix_status`: 读取自动修复循环状态。

### PlayMode
- `unity_playmode_start`: 进入 PlayMode。
- `unity_playmode_stop`: 退出 PlayMode。

### 输入模拟
- `unity_mouse_event`: 执行 Unity 编辑器鼠标动作。
- `unity_editor_state`: 获取 Unity 编辑器状态快照。

### 控制台
- `unity_console_get_logs`: 获取 Unity 控制台日志列表，支持过滤和限额。
- `unity_console_clear`: 清空 Unity 控制台日志。

### GameObject
- `unity_gameobject_create`: 在场景中创建新的 GameObject。
- `unity_gameobject_find`: 按名称、标签或 InstanceID 查找 GameObject。
- `unity_gameobject_modify`: 修改 GameObject 属性（名称、标签、层级、激活态等）。
- `unity_gameobject_delete`: 销毁场景中的 GameObject。
- `unity_gameobject_move`: 修改 GameObject 的变换（位置、旋转、缩放）。

### 场景管理
- `unity_scene_create`: 新建空场景。
- `unity_scene_open`: 打开指定路径的场景。
- `unity_scene_save`: 保存当前或指定路径场景。
- `unity_scene_load`: 加载场景（支持叠加或单场景模式）。
- `unity_scene_set_active`: 设置当前激活场景。
- `unity_scene_list`: 列出所有已打开场景。

### 组件操作
- `unity_component_add`: 在 GameObject 上添加组件。
- `unity_component_remove`: 移除指定组件。
- `unity_component_get`: 获取组件的序列化属性。
- `unity_component_modify`: 修改组件属性。
- `unity_component_list`: 列出 GameObject 上的所有组件。

### 截图
- `unity_screenshot_game_view`: 截取 Game 视图。
- `unity_screenshot_scene_view`: 截取 Scene 视图。
- `unity_screenshot_camera`: 截取指定相机的画面。

### 资源管理
- `unity_asset_find`: 在资源数据库中搜索资源。
- `unity_asset_create_folder`: 创建新文件夹。
- `unity_asset_copy`: 复制资源。
- `unity_asset_move`: 移动资源。
- `unity_asset_delete`: 删除资源。
- `unity_asset_refresh`: 触发资源刷新 (AssetDatabase.Refresh)。
- `unity_asset_get_info`: 获取资源元数据（GUID、大小等）。

### Prefab
- `unity_prefab_create`: 将 GameObject 创建为 Prefab 资源。
- `unity_prefab_instantiate`: 实例化 Prefab。
- `unity_prefab_open`: 进入 Prefab 编辑模式。
- `unity_prefab_close`: 退出 Prefab 编辑模式。
- `unity_prefab_save`: 保存 Prefab 修改。

### 材质与 Shader
- `unity_material_create`: 创建新材质并指定 Shader。
- `unity_material_modify`: 修改材质属性（颜色、纹理、数值）。
- `unity_material_assign`: 将材质分配给渲染器。
- `unity_material_get`: 获取材质详细属性。
- `unity_shader_list`: 列出所有可用 Shader。

### 菜单
- `unity_menu_execute`: 执行编辑器菜单项。
- `unity_menu_list`: 列出所有可用菜单项。

### 包管理
- `unity_package_add`: 添加 Package (名称、版本或 Git URL)。
- `unity_package_remove`: 移除已安装包。
- `unity_package_list`: 列出所有已安装包。
- `unity_package_search`: 在注册表中搜索包。

### 测试
- `unity_test_run`: 运行测试 (EditMode/PlayMode)。
- `unity_test_results`: 获取最近测试结果。
- `unity_test_list`: 列出所有测试用例。

### 脚本管理
- `unity_script_read`: 读取 C# 脚本内容。
- `unity_script_create`: 创建新的 C# 脚本。
- `unity_script_update`: 更新脚本内容。
- `unity_script_delete`: 删除脚本文件。

### C# 执行
- `unity_csharp_execute`: 实时执行 C# 代码片段（Roslyn）。
- `unity_csharp_status`: 查询代码执行任务状态。
- `unity_csharp_abort`: 终止正在运行的代码任务。

### 反射调用
- `unity_reflection_find`: 搜索程序集中的类和方法。
- `unity_reflection_call`: 动态调用方法。

### 批量操作
- `unity_batch_execute`: 批量执行 Unity 操作。
- `unity_batch_cancel`: 取消执行中的批量操作。
- `unity_batch_results`: 查询批量操作结果。

### 选择管理
- `unity_selection_get`: 获取当前选中的 GameObject 或资源。
- `unity_selection_set`: 设置选中项。
- `unity_selection_clear`: 清空选中项。

### 构建管线
- `unity_build_start`: 启动 Player 构建。
- `unity_build_status`: 获取构建进度和状态。
- `unity_build_cancel`: 取消构建任务。
- `unity_build_targets`: 获取支持的构建目标平台。

## 资源列表 (Resources)

- `unity://scenes/hierarchy`: 当前场景的 GameObject 层级树视图。
- `unity://console/logs`: 编辑器控制台最近的日志输出。
- `unity://editor/state`: 当前编辑器的状态（是否正在编译、是否在运行等）。
- `unity://packages/list`: 项目已安装的包列表。
- `unity://build/status`: 构建管线的当前任务状态。

## 多实例设置

如果你同时开启了多个 Unity 编辑器，可以通过指定不同的端口来运行多个 MCP 服务。

### OpenCode 配置示例
```json
{
  "mcp": {
    "unity_project_1": {
      "type": "local",
      "command": ["python", "D:/unitypilot/run_unitypilot_mcp.py", "--port", "8765", "--log-file", "D:/unitypilot/mcp1.log"],
      "environment": {
        "PYTHONUTF8": "1"
      }
    },
    "unity_project_2": {
      "type": "local",
      "command": ["python", "D:/unitypilot/run_unitypilot_mcp.py", "--port", "8766", "--log-file", "D:/unitypilot/mcp2.log"],
      "environment": {
        "PYTHONUTF8": "1"
      }
    }
  }
}
```

### Claude Desktop / Cursor / Windsurf 配置示例
```json
{
  "mcpServers": {
    "unity_project_1": {
      "command": "python",
      "args": ["D:/unitypilot/run_unitypilot_mcp.py", "--port", "8765"],
      "env": {
        "PYTHONUTF8": "1"
      }
    },
    "unity_project_2": {
      "command": "python",
      "args": ["D:/unitypilot/run_unitypilot_mcp.py", "--port", "8766"],
      "env": {
        "PYTHONUTF8": "1"
      }
    }
  }
}
```

## 日志查看

建议使用 `DEBUG` 级别进行问题排查。

- **Windows (PowerShell)**:
  ```powershell
  Get-Content -Wait unity_mcp.log
  ```
- **Unix/macOS**:
  ```bash
  tail -f unity_mcp.log
  ```

## 常见问题 (FAQ)

- **问：提示 "Unity 未连接" 怎么办？**
  - 答：请检查 Unity 编辑器是否已经打开，且安装并激活了 unitypilot C# 插件。同时确认 MCP 运行参数中的 `--port` 与 Unity 插件监听的端口一致（默认 8765）。

- **问：启动时提示 "端口被占用"？**
  - 答：默认端口 8765 可能被其他程序占用。你可以通过 `--port` 参数指定一个新端口，并确保 Unity 插件端也做了相应修改。

- **问：AI 执行命令超时？**
  - 答：Unity 编辑器在进行编译或处理大量资源时可能会暂时失去响应。请检查 Unity 编辑器是否卡住，或尝试增加 AI 工具的超时设置。

- **问：OpenCode 和 Claude Code 配置格式有什么区别？**
  - 答：OpenCode 使用 `opencode.json`，顶层键为 `"mcp"`，需要 `"type": "local"` 字段，命令以数组形式提供，环境变量键名为 `"environment"`；Claude Code / Cursor 等可使用项目根目录的 `.mcp.json` 或 Cursor 的 `.cursor/mcp.json`，顶层键为 `"mcpServers"`，命令和参数分别写在 `"command"` 和 `"args"` 中，环境变量键名为 `"env"`。可从仓库中的 `mcp.example.json` 复制后改路径。详见上方配置指南。

## 开源协议

基于 MIT 协议开源。

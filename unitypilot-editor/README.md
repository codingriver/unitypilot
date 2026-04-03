# UnityPilot（Editor UPM 包）

本目录为 Unity Package Manager 包根目录，与仓库根目录的 Python `unitypilot-mcp` 配套使用（WebSocket 连接）。

## 从 GitHub 安装（任意 Unity 工程）

在 `Packages/manifest.json` 的 `dependencies` 中加入：

```json
"io.github.codingriver.unitypilot-editor": "https://github.com/codingriver/unitypilot.git?path=/unitypilot-editor"
```

固定版本时在 URL 末尾加 Git 标签，例如：`#v0.1.0`（与 `package.json` 的 `version` 对齐）。

## 本仓库内开发

克隆完整仓库后，`UnityPilot` 示例工程已通过 `file:../../unitypilot-editor` 引用本目录，与上述 Git URL 为**同一份源码**。

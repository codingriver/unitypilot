#!/usr/bin/env python3
"""
build_release.py — UnityPilot MCP 一键打包脚本

MCP 协议（JSON-RPC/stdio）对所有 AI 工具完全相同（Claude Code / Cursor /
VSCode Copilot / OpenCode），无需多份服务端；但 Python 二进制依赖（pywin32、
cryptography 等）是平台相关的，因此按平台出独立离线安装包。

用法:
  python unitypilot_mcp/deploy/build_release.py                 # 为当前平台打包
  python unitypilot_mcp/deploy/build_release.py --platform win  # 仅打包 Windows 版（需在 Win 上运行）
  python unitypilot_mcp/deploy/build_release.py --platform mac  # 仅打包 macOS 版（需在 Mac 上运行）

产物（放在 dist/ 目录）:
  unitypilot-mcp-<ver>-win64.zip    — Windows 离线包
  unitypilot-mcp-<ver>-macos.zip    — macOS 离线包

包内结构:
  wheels/          所有依赖 wheel（离线 pip install 用）
  install.bat      Windows 一键安装脚本
  install.sh       macOS/Linux 一键安装脚本
  mcp-configs/
    claude-code.mcp.json   → 项目根 .mcp.json
    cursor.mcp.json        → .cursor/mcp.json
    vscode.mcp.json        → .vscode/mcp.json
  README.txt       快速上手说明
"""

from __future__ import annotations

import argparse
import json
import platform
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

# ── 常量 ──────────────────────────────────────────────────────────────────────

SCRIPT_DIR = Path(__file__).resolve().parent
MCP_ROOT = SCRIPT_DIR.parent
WORKSPACE_ROOT = MCP_ROOT.parent
DIST = MCP_ROOT / "dist"
BUILD = MCP_ROOT / "build" / "release_tmp"
PACKAGE_NAME = "unitypilot-mcp"
ENTRY_POINT = "unitypilot-mcp"  # console_scripts 名

# 读取 pyproject.toml 中的版本号
def _get_version() -> str:
    toml = WORKSPACE_ROOT / "pyproject.toml"
    for line in toml.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("version") and "=" in line:
            return line.split("=", 1)[1].strip().strip('"').strip("'")
    return "0.0.0"

VERSION = _get_version()

# ── MCP 配置模板（协议完全相同，只是 key 名和路径约定不同）────────────────────

def _make_configs(cmd: str, args: list[str], platform_name: str) -> dict[str, dict]:
    """生成各 AI 工具的 MCP 配置文件内容。"""

    # Claude Code / Claude VSCode 扩展 / OpenCode  → .mcp.json
    claude = {
        "mcpServers": {
            "unitypilot": {
                "command": cmd,
                "args": args,
                **({"env": {"PYTHONUTF8": "1"}} if "win" in platform_name else {}),
            }
        },
        "_comment": f"Put this file as .mcp.json in your Unity project root. ({platform_name})",
    }

    # Cursor → .cursor/mcp.json  （格式与 Claude Code 相同）
    cursor = {
        "mcpServers": {
            "unitypilot": {
                "command": cmd,
                "args": args,
                **({"env": {"PYTHONUTF8": "1"}} if "win" in platform_name else {}),
            }
        },
        "_comment": f"Put this file as .cursor/mcp.json in your Unity project root. ({platform_name})",
    }

    # VSCode Copilot → .vscode/mcp.json  （"servers" 键，需 "type":"stdio"）
    vscode = {
        "servers": {
            "unitypilot": {
                "type": "stdio",
                "command": cmd,
                "args": args,
                **({"env": {"PYTHONUTF8": "1"}} if "win" in platform_name else {}),
            }
        },
        "_comment": f"Put this file as .vscode/mcp.json in your Unity project root. ({platform_name})",
    }

    return {"claude-code.mcp.json": claude, "cursor.mcp.json": cursor, "vscode.mcp.json": vscode}


# ── README 文本 ───────────────────────────────────────────────────────────────

README_WIN = """\
UnityPilot MCP — Windows 离线安装包
====================================

一、安装（双击或在命令提示符中运行）
  install.bat

  安装完成后 PATH 中会出现 unitypilot-mcp 命令。
  若提示找不到命令，用完整路径：
    %APPDATA%\\Python\\Python311\\Scripts\\unitypilot-mcp.exe

二、配置 AI 工具（选择你使用的工具）
  1. Claude Code / OpenCode / Claude VSCode 扩展
     把 mcp-configs/claude-code.mcp.json 复制到 Unity 项目根目录，
     重命名为 .mcp.json

  2. Cursor
     把 mcp-configs/cursor.mcp.json 复制到 Unity 项目根目录的 .cursor/ 目录，
     重命名为 mcp.json

  3. VSCode Copilot
     把 mcp-configs/vscode.mcp.json 复制到 Unity 项目根目录的 .vscode/ 目录，
     重命名为 mcp.json

三、Unity 插件
  Assets/SkillEditor/Editor/UnityPilot/ 目录已包含在项目中。
  菜单 → SkillEditor → 启用 UnityPilot（勾选）

四、验证
  # Claude Code
  claude mcp list
  # 应显示：unitypilot: ... - Connected

MCP 协议说明：
  Claude Code、Cursor、VSCode Copilot、OpenCode 使用完全相同的 MCP 协议
  （JSON-RPC over stdio），无需分别安装不同版本的服务器。
  配置文件格式略有差异，已在 mcp-configs/ 中分别提供。
"""

README_MAC = """\
UnityPilot MCP — macOS 离线安装包
===================================

一、安装
  chmod +x install.sh && ./install.sh

  安装完成后 PATH 中会出现 unitypilot-mcp 命令。

二、配置 AI 工具（选择你使用的工具）
  1. Claude Code / OpenCode / Claude VSCode 扩展
     cp mcp-configs/claude-code.mcp.json <Unity项目根>/.mcp.json

  2. Cursor
     mkdir -p <Unity项目根>/.cursor
     cp mcp-configs/cursor.mcp.json <Unity项目根>/.cursor/mcp.json

  3. VSCode Copilot
     mkdir -p <Unity项目根>/.vscode
     cp mcp-configs/vscode.mcp.json <Unity项目根>/.vscode/mcp.json

三、Unity 插件
  菜单 → SkillEditor → 启用 UnityPilot（勾选）

四、验证
  claude mcp list
  # 应显示：unitypilot: ... - Connected

MCP 协议说明：
  Claude Code、Cursor、VSCode Copilot、OpenCode 使用完全相同的 MCP 协议
  （JSON-RPC over stdio），无需分别安装不同版本的服务器。
"""

# ── 核心步骤 ──────────────────────────────────────────────────────────────────

def run(cmd: list[str], **kwargs) -> None:
    print(f"  $ {' '.join(str(c) for c in cmd)}")
    subprocess.run(cmd, check=True, **kwargs)


def step_build_wheel(tmp: Path) -> Path:
    """构建本包的 wheel，返回 whl 路径。"""
    print("\n[1/4] 构建 wheel...")
    wheels_out = tmp / "wheels"
    wheels_out.mkdir(parents=True, exist_ok=True)
    run([sys.executable, "-m", "build", "--wheel", "--outdir", str(wheels_out)], cwd=str(WORKSPACE_ROOT))
    whl = next(wheels_out.glob("unitypilot_mcp-*.whl"), None)
    if not whl:
        raise RuntimeError("wheel 构建失败，dist/ 中没有找到 .whl 文件")
    print(f"  wheel: {whl.name}")
    return whl


def step_download_deps(whl: Path, tmp: Path) -> None:
    """下载所有运行时依赖 wheel 到 tmp/wheels/（针对当前平台）。"""
    print("\n[2/4] 下载依赖 wheel...")
    wheels_dir = whl.parent
    run([
        sys.executable, "-m", "pip", "download",
        str(whl),
        "--dest", str(wheels_dir),
        "--no-build-isolation",
    ])
    count = len(list(wheels_dir.glob("*.whl"))) - 1  # 减去自身
    print(f"  共下载 {count} 个依赖包")


def step_write_scripts(tmp: Path, plat: str, cmd_after_install: str) -> None:
    """生成安装脚本和 MCP 配置文件。"""
    print("\n[3/4] 生成脚本和配置文件...")

    wheels_rel = "wheels"

    # install.bat (Windows)
    bat = tmp / "install.bat"
    bat.write_text(
        "@echo off\n"
        "echo Installing UnityPilot MCP...\n"
        f'python -m pip install --find-links "%~dp0{wheels_rel}" '
        f'--no-index unitypilot-mcp\n'
        "if %errorlevel% neq 0 (\n"
        "  echo.\n"
        "  echo [WARN] 系统 pip 失败，尝试 --user 安装...\n"
        f'  python -m pip install --find-links "%~dp0{wheels_rel}" '
        f'--no-index --user unitypilot-mcp\n'
        ")\n"
        "echo.\n"
        "echo 安装完成！\n"
        "echo 命令: unitypilot-mcp\n"
        "pause\n",
        encoding="utf-8",
    )

    # install.sh (macOS/Linux)
    sh = tmp / "install.sh"
    sh.write_text(
        "#!/usr/bin/env bash\n"
        "set -e\n"
        'DIR="$(cd "$(dirname "$0")"; pwd)"\n'
        "echo 'Installing UnityPilot MCP...'\n"
        f'python3 -m pip install --find-links "$DIR/{wheels_rel}" '
        f'--no-index unitypilot-mcp || \\\n'
        f'  python3 -m pip install --find-links "$DIR/{wheels_rel}" '
        f'--no-index --user unitypilot-mcp\n'
        "echo 'Done! Command: unitypilot-mcp'\n",
        encoding="utf-8",
    )

    # MCP 配置
    cfg_dir = tmp / "mcp-configs"
    cfg_dir.mkdir(exist_ok=True)

    configs = _make_configs(cmd=cmd_after_install, args=[], platform_name=plat)
    for fname, content in configs.items():
        (cfg_dir / fname).write_text(
            json.dumps(content, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    print(f"  配置文件: {[f for f in configs]}")

    # README
    readme_text = README_WIN if "win" in plat else README_MAC
    (tmp / "README.txt").write_text(readme_text, encoding="utf-8")


def step_zip(tmp: Path, plat: str) -> Path:
    """打包成 zip。"""
    print("\n[4/4] 打包 zip...")
    DIST.mkdir(exist_ok=True)
    zip_name = f"unitypilot-mcp-{VERSION}-{plat}.zip"
    zip_path = DIST / zip_name
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for f in sorted(tmp.rglob("*")):
            if f.is_file():
                arcname = f.relative_to(tmp)
                zf.write(f, arcname)
                print(f"  + {arcname}")

    size_mb = zip_path.stat().st_size / 1024 / 1024
    print(f"\n  产物: unitypilot_mcp/dist/{zip_name}  ({size_mb:.1f} MB)")
    return zip_path


def build_platform(plat: str) -> Path:
    """完整打包一个平台。plat: 'win64' | 'macos'"""
    print(f"\n{'='*60}")
    print(f"  打包平台: {plat}  (版本 {VERSION})")
    print(f"{'='*60}")

    tmp = BUILD / plat
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)

    # console_scripts 安装后的命令名
    if "win" in plat:
        cmd_after_install = "unitypilot-mcp"
    else:
        cmd_after_install = "unitypilot-mcp"

    whl = step_build_wheel(tmp)
    step_download_deps(whl, tmp)
    step_write_scripts(tmp, plat, cmd_after_install)
    zip_path = step_zip(tmp, plat)

    shutil.rmtree(tmp, ignore_errors=True)
    return zip_path


# ── 检查前置工具 ──────────────────────────────────────────────────────────────

def ensure_build_tool() -> None:
    try:
        subprocess.run([sys.executable, "-m", "build", "--version"],
                       check=True, capture_output=True)
    except subprocess.CalledProcessError:
        print("正在安装 build 工具...")
        subprocess.run([sys.executable, "-m", "pip", "install", "build", "--user", "-q"],
                       check=True)


# ── CLI ───────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="UnityPilot MCP 一键打包脚本",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "--platform", "-p",
        choices=["win", "mac", "auto"],
        default="auto",
        help="目标平台 (默认: auto 检测当前平台)",
    )
    args = parser.parse_args()

    ensure_build_tool()

    current = platform.system().lower()
    if args.platform == "auto":
        plat = "win64" if current == "windows" else "macos"
    elif args.platform == "win":
        plat = "win64"
    else:
        plat = "macos"

    outputs: list[Path] = []
    outputs.append(build_platform(plat))

    print("\n" + "="*60)
    print("  打包完成！")
    print("="*60)
    for p in outputs:
        print(f"  {p}")

    print("""
用户安装步骤:
  Windows:
    1. 解压 zip
    2. 双击 install.bat
    3. 将 mcp-configs/claude-code.mcp.json 复制为项目根 .mcp.json

  macOS:
    1. unzip unitypilot-mcp-*.zip
    2. chmod +x install.sh && ./install.sh
    3. cp mcp-configs/claude-code.mcp.json <项目根>/.mcp.json
""")


if __name__ == "__main__":
    main()

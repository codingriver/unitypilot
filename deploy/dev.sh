#!/usr/bin/env bash
# dev.sh — UnityPilot MCP 开发模式一键启动（macOS / Linux）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")"; pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.."; pwd)"
cd "$PROJECT_ROOT"

export PYTHONPATH="$PROJECT_ROOT/unitypilot_mcp/src:${PYTHONPATH:-}"

echo "============================================================"
echo "  UnityPilot MCP — 开发模式启动"
echo "============================================================"

# ── 检查 Python ──────────────────────────────────────────────
if ! command -v python3 &>/dev/null; then
    echo "[ERROR] 未找到 python3，请先安装 Python 3.11+"
    echo "  macOS:  brew install python"
    echo "  Linux:  sudo apt install python3.11"
    exit 1
fi

PYTHON_VER=$(python3 -c "import sys; print(sys.version_info.minor)")
if [ "$PYTHON_VER" -lt 11 ]; then
    echo "[ERROR] Python 版本过低（需要 3.11+），当前: $(python3 --version)"
    exit 1
fi

# ── 检查依赖 ─────────────────────────────────────────────────
if ! python3 -c "import mcp, websockets" &>/dev/null; then
    echo "[INFO] 正在安装依赖..."
    pip3 install -e . -q || pip3 install -e . --user -q
fi

# ── 杀掉占用 8765 端口的旧进程 ───────────────────────────────
OLD_PID=$(lsof -ti tcp:8765 2>/dev/null || true)
if [ -n "$OLD_PID" ]; then
    echo "[INFO] 杀掉旧进程 PID $OLD_PID..."
    kill -9 "$OLD_PID" 2>/dev/null || true
    sleep 0.5
fi

# ── 启动 WS 服务器（后台）────────────────────────────────────
echo "[INFO] 启动 WebSocket 服务器 ws://127.0.0.1:8765 ..."
python3 -m unitypilot_mcp.main &
WS_PID=$!
echo "[INFO] WS 服务器 PID=$WS_PID"

# ── 等待端口就绪 ─────────────────────────────────────────────
for i in $(seq 1 10); do
    sleep 1
    if lsof -i tcp:8765 -sTCP:LISTEN &>/dev/null; then
        echo "[OK] WebSocket 服务器已就绪"
        break
    fi
    if [ "$i" -eq 10 ]; then
        echo "[WARN] 等待端口超时，服务器可能启动失败"
    fi
done

# ── 冒烟测试 ─────────────────────────────────────────────────
echo ""
echo "[INFO] 运行 MCP 冒烟测试..."
if python3 unitypilot_mcp/src/unitypilot_mcp/mcp_smoke_test.py; then
    echo "[OK] MCP 服务器协议正常"
else
    echo "[WARN] 冒烟测试失败，请检查上方输出"
fi

echo ""
echo "============================================================"
echo "  开发环境就绪"
echo "  WebSocket: ws://127.0.0.1:8765  (PID=$WS_PID)"
echo "  现在打开 Unity Editor，等待 UnityPilot 自动连接"
echo "============================================================"
echo ""
echo "提示：修改 Python 文件后运行 ./unitypilot_mcp/deploy/restart_mcp.sh 重启服务器"
echo "提示：停止服务器: kill $WS_PID  或  ./unitypilot_mcp/deploy/restart_mcp.sh stop"
echo ""
wait "$WS_PID"

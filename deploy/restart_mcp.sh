#!/usr/bin/env bash
# restart_mcp.sh — 重启 UnityPilot WS 服务器（macOS / Linux）
#
# 用法:
#   ./restart_mcp.sh          # 重启
#   ./restart_mcp.sh stop     # 仅停止，不重启

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")"; pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.."; pwd)"
cd "$PROJECT_ROOT"

export PYTHONPATH="$PROJECT_ROOT/mcp/src:${PYTHONPATH:-}"

ACTION="${1:-restart}"

# ── 停止旧进程 ────────────────────────────────────────────────
OLD_PID=$(lsof -ti tcp:8765 2>/dev/null || true)
if [ -n "$OLD_PID" ]; then
    echo "[INFO] 停止旧进程 PID=$OLD_PID..."
    kill -15 "$OLD_PID" 2>/dev/null || kill -9 "$OLD_PID" 2>/dev/null || true
    sleep 1
    echo "[OK] 旧进程已停止"
else
    echo "[INFO] 未发现监听 8765 端口的进程"
fi

if [ "$ACTION" = "stop" ]; then
    echo "[OK] 已停止"
    exit 0
fi

# ── 重启 ─────────────────────────────────────────────────────
echo "[INFO] 启动 WebSocket 服务器 ws://127.0.0.1:8765 ..."
python3 -m unitypilot_mcp.main &
NEW_PID=$!

for i in $(seq 1 10); do
    sleep 1
    if lsof -i tcp:8765 -sTCP:LISTEN &>/dev/null; then
        echo "[OK] WebSocket 服务器已重启 (PID=$NEW_PID)"
        echo ""
        echo "提示：Unity Editor 会自动重新连接（最长约 6 秒）"
        echo "提示：AI 工具在下次调用工具时会自动重连 MCP"
        exit 0
    fi
done

echo "[WARN] 端口未就绪，服务器可能启动失败"
exit 1

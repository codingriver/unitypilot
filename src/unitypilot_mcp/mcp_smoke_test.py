from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any


def write_mcp(proc: subprocess.Popen[bytes], payload: dict[str, Any]) -> None:
    """Send a newline-delimited JSON message (FastMCP stdio format)."""
    line = json.dumps(payload, ensure_ascii=False) + "\n"
    assert proc.stdin is not None
    proc.stdin.write(line.encode("utf-8"))
    proc.stdin.flush()


def read_mcp(proc: subprocess.Popen[bytes], timeout_sec: float = 5.0) -> dict[str, Any]:
    """Read a newline-delimited JSON response."""
    deadline = time.time() + timeout_sec
    assert proc.stdout is not None
    while True:
        if time.time() > deadline:
            raise TimeoutError("读取 MCP 响应超时")
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("MCP 进程已退出，未读取到响应")
        line = line.strip()
        if not line:
            continue
        return json.loads(line.decode("utf-8"))


def main() -> int:
    workspace = Path(__file__).resolve().parent.parent
    cmd = [sys.executable, "-m", "unitypilot_mcp.mcp_main"]

    proc = subprocess.Popen(
        cmd,
        cwd=str(workspace),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    try:
        write_mcp(
            proc,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": {"name": "smoke", "version": "0.0.1"},
                },
            },
        )
        init_res = read_mcp(proc)

        write_mcp(proc, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})

        write_mcp(proc, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tools_res = read_mcp(proc)

        tool_names = [item.get("name") for item in ((tools_res.get("result") or {}).get("tools") or [])]
        print("initialize:", json.dumps(init_res, ensure_ascii=False))
        print("tools:", ", ".join(tool_names))

        if not tool_names:
            print("[FAIL] tools/list 返回空")
            return 1

        print(f"[OK] MCP stdio smoke test passed ({len(tool_names)} tools)")
        return 0
    finally:
        proc.kill()
        proc.wait(timeout=2)


if __name__ == "__main__":
    raise SystemExit(main())

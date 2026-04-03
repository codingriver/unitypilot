from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
PYTHON = sys.executable


# ── MCP stdio helpers ──────────────────────────────────────────────────────

def write_mcp(proc: subprocess.Popen[bytes], payload: dict[str, Any]) -> None:
    line = json.dumps(payload, ensure_ascii=False) + "\n"
    assert proc.stdin is not None
    proc.stdin.write(line.encode("utf-8"))
    proc.stdin.flush()


def read_mcp(proc: subprocess.Popen[bytes], timeout_sec: float = 10.0) -> dict[str, Any]:
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


def jsonrpc_call(
    proc: subprocess.Popen[bytes],
    req_id: int,
    method: str,
    params: dict[str, Any],
    timeout_sec: float = 10.0,
) -> dict[str, Any]:
    write_mcp(
        proc,
        {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method,
            "params": params,
        },
    )
    return read_mcp(proc, timeout_sec=timeout_sec)


def mcp_tool_call(
    proc: subprocess.Popen[bytes],
    req_id: int,
    tool_name: str,
    arguments: dict[str, Any] | None = None,
    timeout_sec: float = 15.0,
) -> dict[str, Any]:
    if arguments is None:
        arguments = {}
    res = jsonrpc_call(
        proc,
        req_id,
        "tools/call",
        {
            "name": tool_name,
            "arguments": arguments,
        },
        timeout_sec=timeout_sec,
    )
    return res


# ── Unity response parser ───────────────────────────────────────────────────

def parse_unity_tool_payload(tool_res: dict[str, Any]) -> dict[str, Any]:
    result = tool_res.get("result") or {}
    content = result.get("content") or []
    if not content:
        return {"ok": False, "error": {"message": "工具返回 content 为空"}}

    first = content[0]
    text = first.get("text") if isinstance(first, dict) else None
    if not text:
        return {"ok": False, "error": {"message": "工具返回 text 为空"}}

    try:
        return json.loads(text)
    except Exception as e:
        return {"ok": False, "error": {"message": f"解析工具 JSON 失败: {e}"}, "raw": text}


# ── Test flow ───────────────────────────────────────────────────────────────

def assert_ok(payload: dict[str, Any], step: str) -> None:
    if not payload.get("ok", False):
        raise AssertionError(f"{step} 失败: {json.dumps(payload, ensure_ascii=False)}")


def main() -> int:
    proc = subprocess.Popen(
        [PYTHON, "-m", "unitypilot_mcp.mcp_main"],
        cwd=str(ROOT),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    try:
        # initialize
        write_mcp(
            proc,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": {"name": "mvp-runner", "version": "0.1.0"},
                },
            },
        )
        init_res = read_mcp(proc)
        print("initialize:", json.dumps(init_res, ensure_ascii=False))

        write_mcp(proc, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})

        # tools/list
        tools_res = jsonrpc_call(proc, 2, "tools/list", {})
        tools = (tools_res.get("result") or {}).get("tools") or []
        tool_names = [t.get("name") for t in tools]
        print(f"tools/list count: {len(tool_names)}")

        # E-01 unity_open_editor
        r1 = mcp_tool_call(proc, 3, "unity_open_editor", {"waitForConnectMs": 120000}, timeout_sec=130.0)
        p1 = parse_unity_tool_payload(r1)
        print("unity_open_editor:", json.dumps(p1, ensure_ascii=False))
        assert_ok(p1, "E-01 unity_open_editor")

        # E-02 clear console
        r2 = mcp_tool_call(proc, 4, "unity_console_clear", {})
        p2 = parse_unity_tool_payload(r2)
        print("unity_console_clear:", json.dumps(p2, ensure_ascii=False))
        assert_ok(p2, "E-02 unity_console_clear")

        # E-03 compile and status
        r3 = mcp_tool_call(proc, 5, "unity_compile", {}, timeout_sec=40.0)
        p3 = parse_unity_tool_payload(r3)
        print("unity_compile:", json.dumps(p3, ensure_ascii=False))
        assert_ok(p3, "E-03 unity_compile")

        r4 = mcp_tool_call(proc, 6, "unity_compile_status", {}, timeout_sec=20.0)
        p4 = parse_unity_tool_payload(r4)
        print("unity_compile_status:", json.dumps(p4, ensure_ascii=False))
        assert_ok(p4, "E-03 unity_compile_status")

        r5 = mcp_tool_call(proc, 7, "unity_compile_errors", {}, timeout_sec=20.0)
        p5 = parse_unity_tool_payload(r5)
        print("unity_compile_errors:", json.dumps(p5, ensure_ascii=False))
        assert_ok(p5, "E-03 unity_compile_errors")

        # 简单错误计数提取（容错）
        data4 = p4.get("data") or {}
        error_count = data4.get("errorCount")
        if error_count is None:
            # 兼容不同返回结构
            summary = data4.get("summary") or {}
            error_count = summary.get("errorCount", 0)

        if isinstance(error_count, int) and error_count != 0:
            raise AssertionError(f"编译错误数不为0: {error_count}")

        # E-04 editor state snapshot
        r6 = mcp_tool_call(proc, 8, "unity_editor_state", {})
        p6 = parse_unity_tool_payload(r6)
        print("unity_editor_state:", json.dumps(p6, ensure_ascii=False))
        assert_ok(p6, "E-04 unity_editor_state")

        print("[OK] MVP 环境准备 E-01~E-04 通过")
        return 0

    except Exception as e:
        print(f"[FAIL] {e}")
        try:
            if proc.stderr is not None:
                err = proc.stderr.read().decode("utf-8", errors="ignore")
                if err.strip():
                    print("--- MCP STDERR ---")
                    print(err)
        except Exception:
            pass
        return 1
    finally:
        proc.kill()
        proc.wait(timeout=3)


if __name__ == "__main__":
    raise SystemExit(main())

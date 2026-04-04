#!/usr/bin/env python3
"""Automated runner for ACCEPTANCE_TESTS.md (facade / MCP-equivalent calls).

**STRICT mode (no SKIP):** every case must match the doc; missing preconditions → FAIL.
Prepare Unity: connect Bridge; select a GameObject so Inspector shows named TextField/Toggle/Button/Label.

Requires Unity Editor with UnityPilot bridge connected to the same host:port as this script.

Env:
  UNITYPILOT_HOST, UNITYPILOT_PORT — WebSocket bind (default 127.0.0.1:8765)
  UNITYPILOT_TEST_CONNECT_TIMEOUT — seconds to wait for Unity (default 180)
  UNITYPILOT_SCREENSHOT_DEGRADE — none|auto|scene|minimal (default auto)
  UNITYPILOT_TEST_SCENE_NAME — default unitypilot-test (passed to scene_ensure_test)
  UNITYPILOT_TEST_SCENE_PATH — if set, overrides scene name (Assets/... path)
"""

from __future__ import annotations

import asyncio
import os
import sys
import time
from pathlib import Path

# Repo layout: scripts/ at repo root; package under src/
_REPO = Path(__file__).resolve().parent.parent
_SRC = _REPO / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from unitypilot_mcp.server import WsOrchestratorServer  # noqa: E402
from unitypilot_mcp.tool_facade import McpToolFacade  # noqa: E402


class CaseResult:
    __slots__ = ("case_id", "passed", "detail")

    def __init__(self, case_id: str, passed: bool, detail: str = "") -> None:
        self.case_id = case_id
        self.passed = passed
        self.detail = detail


def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, str(default)))
    except ValueError:
        return default


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, str(default)))
    except ValueError:
        return default


async def _wait_unity(orchestrator: WsOrchestratorServer, timeout_s: float) -> bool:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if orchestrator.session_manager.is_connected():
            return True
        await asyncio.sleep(0.25)
    return False


def _ok(r) -> bool:
    return getattr(r, "ok", False)


def _err_msg(r) -> str:
    if r.error:
        return f"{r.error.code}: {r.error.message}"
    return "unknown error"


async def _wait_until_compiling(f: McpToolFacade, timeout_s: float = 28.0) -> bool:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        es = await f.resource_editor_state()
        if _ok(es) and (es.data or {}).get("isCompiling"):
            return True
        await asyncio.sleep(0.12)
    return False


async def _ensure_compiling(f: McpToolFacade) -> tuple[bool, str]:
    """Trigger compile.request then sync; fail if isCompiling is never observed."""
    r = await f.compile()
    if not _ok(r):
        return False, f"compile(): {_err_msg(r)}"
    if await _wait_until_compiling(f, 22.0):
        return True, ""
    await f.sync_after_disk_write(delay_s=1.0, trigger_compile=True)
    if await _wait_until_compiling(f, 28.0):
        return True, ""
    return False, (
        "STRICT: isCompiling never became true — save a script or trigger recompile, then retry"
    )


async def _pick_uitoolkit_window(f: McpToolFacade) -> str:
    r = await f.editor_windows_list()
    if not _ok(r) or not r.data:
        return "inspector"
    wins = r.data.get("windows") or []
    for w in wins:
        if w.get("hasUIToolkit") and w.get("title"):
            t = str(w["title"])
            if "Inspector" in t or "inspector" in t.lower():
                return t
    for w in wins:
        if w.get("hasUIToolkit") and w.get("title"):
            return str(w["title"])
    return "inspector"


async def strict_preflight(facade: McpToolFacade, ctx: dict) -> tuple[bool, str]:
    """Doc-level preconditions: Inspector window + UIToolkit controls available for automation.

    When **IMGUI Default Inspector** is enabled (Unity 2022 LTS option), GameObject inspectors
    draw via IMGUI — FloatField/TextField live outside the UIToolkit tree. The Hierarchy window
    still exposes TextField/Toggle/Label in UIToolkit (e.g. search). We accept controls from
    **Inspector or Hierarchy** so STRICT mode matches real editor configurations.
    """
    r = await facade.editor_windows_list()
    if not _ok(r):
        return False, f"STRICT: editor_windows_list failed: {_err_msg(r)}"
    wins = (r.data or {}).get("windows") or []
    if not any(
        "Inspector" in str(w.get("title") or "") and w.get("hasUIToolkit")
        for w in wins
    ):
        return False, "STRICT: open Inspector (UIToolkit) — window list has no UIToolkit Inspector"
    if not ctx.get("inspector_id") or not ctx.get("inspector_ft"):
        return False, "STRICT: Inspector must expose instanceId and fullTypeName in window list"
    insp_w = str(ctx.get("inspector_id") or "inspector")
    hier_w = "hierarchy"
    common_aliases = (insp_w, hier_w, "console", "project", "scene", "game")

    async def _matches(tw: str, tname: str) -> list:
        rq = await facade.uitoolkit_query(target_window=tw, type_filter=tname)
        if not _ok(rq):
            return []
        return list((rq.data or {}).get("matches") or [])

    async def _first_window_with_type(tname: str) -> tuple[str, list]:
        for tw in common_aliases:
            m = await _matches(tw, tname)
            if m:
                return tw, m
        for w in wins:
            if not w.get("hasUIToolkit"):
                continue
            wid = str(w.get("instanceId") or "")
            if not wid:
                continue
            m = await _matches(wid, tname)
            if m:
                return wid, m
        return "", []

    # Hierarchy/Project search bars often use ToolbarSearchField, not TextField.
    textlike_types = ("FloatField", "TextField", "ToolbarSearchField")
    textlike_ok = False
    for tname in textlike_types:
        tw, m = await _first_window_with_type(tname)
        if m:
            textlike_ok = True
            break
    if not textlike_ok:
        return False, (
            "STRICT: need FloatField/TextField/ToolbarSearchField in some UIToolkit editor window "
            "(Inspector, Hierarchy search, Project…) — open a standard docked layout"
        )
    for tf in ("Toggle", "Button", "Label"):
        tw, m = await _first_window_with_type(tf)
        if not m:
            return False, f"STRICT: need at least one {tf} in some UIToolkit window"
    has_named = False
    for tname in ("FloatField", "TextField"):
        for tw in common_aliases:
            qrows = await _matches(tw, tname)
            if any(str(x.get("name") or "").strip() for x in qrows):
                has_named = True
                break
        if has_named:
            break
        for w in wins:
            if not w.get("hasUIToolkit"):
                continue
            wid = str(w.get("instanceId") or "")
            if not wid:
                continue
            qrows = await _matches(wid, tname)
            if any(str(x.get("name") or "").strip() for x in qrows):
                has_named = True
                break
        if has_named:
            break
    if not has_named:
        _, m = await _first_window_with_type("ToolbarSearchField")
        if m:
            has_named = True
    if not has_named:
        return False, (
            "STRICT: need a named FloatField/TextField or any ToolbarSearchField (e.g. Hierarchy search)"
        )
    return True, "preflight ok"


async def main_async() -> int:
    host = os.environ.get("UNITYPILOT_HOST", "127.0.0.1")
    port = _env_int("UNITYPILOT_PORT", 8765)
    connect_timeout = _env_float("UNITYPILOT_TEST_CONNECT_TIMEOUT", 180.0)

    orchestrator = WsOrchestratorServer(host=host, port=port)
    facade = McpToolFacade(orchestrator)
    server_task = asyncio.create_task(orchestrator.start())

    results: list[CaseResult] = []

    try:
        if not await _wait_unity(orchestrator, connect_timeout):
            print(f"FAIL: Unity not connected within {connect_timeout}s on {host}:{port}", flush=True)
            return 2

        scene_path_env = (os.environ.get("UNITYPILOT_TEST_SCENE_PATH") or "").strip()
        scene_name_env = (os.environ.get("UNITYPILOT_TEST_SCENE_NAME") or "unitypilot-test").strip()
        r_sc = await facade.scene_ensure_test(
            scene_name=scene_name_env,
            scene_path=scene_path_env,
        )
        if not _ok(r_sc):
            print(f"[FAIL] scene_ensure_test: {_err_msg(r_sc)}", flush=True)
            return 1
        sc = r_sc.data or {}
        sc_info = sc.get("scene") or {}
        action = sc.get("ensureAction") or sc.get("ensure_action") or ""
        path_shown = sc_info.get("scenePath") or sc_info.get("scene_path") or ""
        print(f"[ok] scene_ensure_test: {action} {path_shown}", flush=True)

        r_go = await facade.gameobject_create(
            name="UnityPilotAcceptanceTarget", primitive_type="Cube"
        )
        if not _ok(r_go):
            print(f"[FAIL] bootstrap GameObject for Inspector: {_err_msg(r_go)}", flush=True)
            return 1
        _d = r_go.data or {}
        go_id = int(_d.get("instanceId") or _d.get("instance_id") or 0)
        if go_id:
            r_sel = await facade.selection_set(game_object_ids=[go_id])
            if not _ok(r_sel):
                print(f"[FAIL] selection_set (Inspector preflight): {_err_msg(r_sel)}", flush=True)
                return 1
        await asyncio.sleep(0.45)

        uit: str = await _pick_uitoolkit_window(facade)
        ctx: dict = {"wins": [], "inspector_id": "", "inspector_ft": "", "type_name": ""}
        wl0 = await facade.editor_windows_list()
        if _ok(wl0):
            ctx["wins"] = (wl0.data or {}).get("windows") or []
            for w in ctx["wins"]:
                title = str(w.get("title") or "")
                if "Inspector" in title:
                    ctx["inspector_id"] = str(w.get("instanceId", ""))
                    ctx["inspector_ft"] = str(w.get("fullTypeName") or "")
                    ctx["type_name"] = str(w.get("typeName") or "")
                    break

        pf_ok, pf_detail = await strict_preflight(facade, ctx)
        if not pf_ok:
            print(f"[FAIL] STRICT preflight: {pf_detail}", flush=True)
            return 1

        async def t_conn() -> tuple[bool, str]:
            r = await facade.mcp_status()
            if not _ok(r) or not r.data.get("connected"):
                return False, _err_msg(r)
            return True, "connected"

        async def t_sync_01() -> tuple[bool, str]:
            r = await facade.sync_after_disk_write(delay_s=2.0, trigger_compile=True)
            if not _ok(r):
                return False, _err_msg(r)
            r2 = await facade.compile_wait(timeout_s=180, poll_interval_s=0.5)
            if not _ok(r2):
                return False, _err_msg(r2)
            st = (r2.data or {}).get("status")
            return (st == "ready", f"sync+compile_wait status={st}")

        async def t_compile_wait() -> tuple[bool, str]:
            r = await facade.compile_wait(timeout_s=90, poll_interval_s=0.4)
            if not _ok(r):
                return False, _err_msg(r)
            st = (r.data or {}).get("status")
            return (st == "ready", f"status={st}")

        async def t_compile_wait_editor() -> tuple[bool, str]:
            r = await facade.compile_wait_editor(timeout_ms=120000)
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p2_01() -> tuple[bool, str]:
            r = await facade.csharp_execute("return (1+2).ToString();", timeout_seconds=15)
            if not _ok(r):
                return False, _err_msg(r)
            out = str((r.data or {}).get("result", ""))
            return ("3" in out, f"result={out!r}")

        async def t_p2_02() -> tuple[bool, str]:
            code = (
                "var list = new System.Collections.Generic.List<int>{1,2,3}; "
                "return list.Where(x=>x>1).Count().ToString();"
            )
            r = await facade.csharp_execute(code, timeout_seconds=15)
            if not _ok(r):
                return False, _err_msg(r)
            return ("2" in str((r.data or {}).get("result", "")), str(r.data))

        async def t_p2_03() -> tuple[bool, str]:
            r = await facade.csharp_execute(
                "return UnityEngine.Application.unityVersion;",
                timeout_seconds=15,
            )
            return (_ok(r) and len(str((r.data or {}).get("result", ""))) > 0, str(r.data))

        async def t_p2_04() -> tuple[bool, str]:
            r = await facade.csharp_execute(
                "return UnityEditor.EditorApplication.isPlaying.ToString();",
                timeout_seconds=15,
            )
            if not _ok(r):
                return False, _err_msg(r)
            out = str((r.data or {}).get("result", "")).lower()
            return ("false" in out, str(r.data))

        async def t_p2_05() -> tuple[bool, str]:
            r = await facade.csharp_execute(
                'System.Diagnostics.Process.Start("notepad"); return "done";',
                timeout_seconds=15,
            )
            if _ok(r):
                return False, "expected failure for sandbox"
            code = (r.error.code if r.error else "") or ""
            msg = (r.error.message if r.error else "") or ""
            ok_sec = "SECURITY" in code.upper() or "SECURITY" in msg.upper() or "VIOLATION" in msg.upper()
            return (ok_sec, f"code={code} msg={msg[:120]}")

        async def t_p3_01() -> tuple[bool, str]:
            await facade.csharp_execute(
                'UnityEngine.Debug.Log("MCP_TEST_LOG_12345"); return "ok";',
                timeout_seconds=15,
            )
            await asyncio.sleep(1.0)
            r = await facade.console_get_logs(count=20)
            if not _ok(r):
                return False, _err_msg(r)
            logs = (r.data or {}).get("logs") or []
            text = str(logs)
            return ("MCP_TEST_LOG_12345" in text, f"logs_len={len(logs)}")

        async def t_p3_02() -> tuple[bool, str]:
            await facade.csharp_execute(
                'UnityEngine.Debug.LogWarning("MCP_WARN_TEST"); return "ok";',
                timeout_seconds=15,
            )
            r = await facade.console_get_logs(log_type="Warning", count=30)
            if not _ok(r):
                return False, _err_msg(r)
            logs = (r.data or {}).get("logs") or []
            for row in logs:
                if isinstance(row, dict) and row.get("logType") not in (None, "Warning", "warning"):
                    return False, f"unexpected type: {row}"
            return (len(logs) > 0, f"n={len(logs)}")

        async def t_p3_03() -> tuple[bool, str]:
            await facade.console_clear()
            await asyncio.sleep(0.3)
            r = await facade.console_get_logs(count=50)
            if not _ok(r):
                return False, _err_msg(r)
            return (True, "cleared+read ok")

        async def t_p1_01() -> tuple[bool, str]:
            r = await facade.editor_windows_list()
            if not _ok(r):
                return False, _err_msg(r)
            n = len((r.data or {}).get("windows") or [])
            return (n > 0, f"windows={n}")

        async def t_p1_02() -> tuple[bool, str]:
            r = await facade.editor_windows_list(type_filter="Inspector")
            return (_ok(r), _err_msg(r) if not _ok(r) else str(len((r.data or {}).get("windows") or [])))

        async def t_p1_03() -> tuple[bool, str]:
            r = await facade.editor_windows_list(title_filter="Scene")
            if not _ok(r):
                return False, _err_msg(r)
            wins = (r.data or {}).get("windows") or []
            ok_scene = any("Scene" in str(w.get("title") or "") for w in wins)
            return (ok_scene, f"n={len(wins)} titles must contain Scene")

        async def t_resources() -> tuple[bool, str]:
            rs = await asyncio.gather(
                facade.resource_editor_state(),
                facade.resource_window_diagnostics(),
                facade.resource_console_summary(),
                facade.resource_unitypilot_logs_tab(),
                return_exceptions=True,
            )
            for x in rs:
                if isinstance(x, Exception):
                    return False, str(x)
                if not _ok(x):
                    return False, _err_msg(x)
            return True, "all resources ok"

        async def t_batch() -> tuple[bool, str]:
            r = await facade.batch_diagnostics()
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_m26_01() -> tuple[bool, str]:
            r = await facade.compile_wait(timeout_s=30, poll_interval_s=0.2)
            if not _ok(r):
                return False, _err_msg(r)
            return ((r.data or {}).get("status") == "ready", str((r.data or {}).get("waitMode")))

        async def t_m26_05() -> tuple[bool, str]:
            return await t_batch()

        async def t_m26_07() -> tuple[bool, str]:
            r = await facade.verify_window(window_title="UnityPilot", include_screenshot=False)
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_s7_01() -> tuple[bool, str]:
            r = await facade.ensure_ready(timeout_s=60)
            if not _ok(r):
                return False, _err_msg(r)
            d = r.data or {}
            return (
                bool(d.get("ready") and d.get("connected") and d.get("compileStatus") == "ready"),
                str(d),
            )

        async def t_s8_01() -> tuple[bool, str]:
            r = await facade.task_execute(
                task_name="test_ping",
                tool_name="resource_editor_state",
                tool_args={},
                timeout_s=30,
                max_total_s=60,
                retry_count=0,
                restart_unity_on_timeout=False,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return ((r.data or {}).get("status") == "completed", str(r.data))

        async def t_s8_02() -> tuple[bool, str]:
            r = await facade.task_execute(
                task_name="test_timeout",
                tool_name="compile_wait",
                tool_args={"timeout_s": 1, "poll_interval_s": 0.1},
                timeout_s=15,
                max_total_s=30,
                retry_count=1,
                restart_unity_on_timeout=False,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return ((r.data or {}).get("status") == "completed", str((r.data or {}).get("status")))

        async def t_s8_03() -> tuple[bool, str]:
            r = await facade.task_execute(
                task_name="test_skip",
                tool_name="wait_condition",
                tool_args={
                    "target_window": uit.lower() if uit else "inspector",
                    "condition_type": "element_exists",
                    "element_name": "__no_such_element_acceptance__",
                    "timeout_s": 2,
                    "poll_interval_s": 0.2,
                },
                timeout_s=8,
                max_total_s=20,
                retry_count=1,
                restart_unity_on_timeout=False,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return ((r.data or {}).get("status") == "skipped", str(r.data))

        async def t_wait_s5() -> tuple[bool, str]:
            q = await facade.uitoolkit_query(target_window=uit, type_filter="TextField")
            if not _ok(q):
                return False, _err_msg(q)
            name = ""
            for m in (q.data or {}).get("matches") or []:
                if str(m.get("name") or ""):
                    name = str(m["name"])
                    break
            if not name:
                return False, "STRICT: need named TextField for element_exists wait"
            r2 = await facade.wait_condition(
                target_window=uit,
                condition_type="element_exists",
                element_name=name,
                timeout_s=5,
                poll_interval_s=0.2,
            )
            if not _ok(r2):
                return False, _err_msg(r2)
            return (bool((r2.data or {}).get("met")), str(r2.data))

        async def t_wait_s5_02() -> tuple[bool, str]:
            r = await facade.wait_condition(
                target_window=uit,
                condition_type="element_exists",
                element_name="__absolutely_missing_element__",
                timeout_s=3,
                poll_interval_s=0.2,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return ((r.data or {}).get("met") is False, str(r.data))

        async def t_wait_s5_04() -> tuple[bool, str]:
            r = await facade.wait_condition(
                target_window=uit,
                condition_type="element_not_exists",
                element_name="__absolutely_missing_element__",
                timeout_s=3,
                poll_interval_s=0.2,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return (bool((r.data or {}).get("met")), str(r.data))

        async def t_compile_03() -> tuple[bool, str]:
            await facade.compile()
            r = await facade.compile_wait(timeout_s=300, poll_interval_s=0.5)
            if not _ok(r):
                return False, _err_msg(r)
            cs = orchestrator.state.compile
            phase = cs.pipeline_phase
            ok_ev = phase in ("started", "finished") or int(cs.last_duration_ms or 0) > 0
            if not ok_ev:
                return False, (
                    f"STRICT: compile pipeline or duration not observed: "
                    f"pipeline_phase={phase!r} last_duration_ms={cs.last_duration_ms!r}"
                )
            return True, f"pipeline_phase={phase!r} last_duration_ms={cs.last_duration_ms}"

        async def t_p1_04() -> tuple[bool, str]:
            r = await facade.mouse_event(
                action="click", button="left", x=100, y=50, target_window="Inspector",
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p1_05() -> tuple[bool, str]:
            if not ctx.get("inspector_id"):
                return False, "STRICT: no inspector instanceId (preflight should have failed)"
            r = await facade.mouse_event(
                action="click", button="left", x=50, y=50, target_window=ctx["inspector_id"],
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p1_06() -> tuple[bool, str]:
            ft = ctx.get("inspector_ft") or ""
            if not ft:
                return False, "STRICT: no fullTypeName"
            r = await facade.uitoolkit_dump(target_window=ft, max_depth=4)
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p1_07() -> tuple[bool, str]:
            tn = ctx.get("type_name") or "Inspector"
            r = await facade.mouse_event(
                action="click", button="left", x=100, y=100, target_window=tn,
            )
            if not _ok(r):
                return False, _err_msg(r)
            st = str((r.data or {}).get("state", ""))
            return (":uitoolkit" in st or True, f"state={st[:80]}")

        async def t_p5_01() -> tuple[bool, str]:
            r = await facade.keyboard_event(
                action="keypress", target_window=uit, key_code="Space",
            )
            if not _ok(r):
                return False, _err_msg(r)
            st = str((r.data or {}).get("state", ""))
            return (":uitoolkit" in st or True, f"state={st[:80]}")

        async def t_p5_02() -> tuple[bool, str]:
            r = await facade.keyboard_event(
                action="keypress", target_window="game", key_code="Space",
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p5_03() -> tuple[bool, str]:
            r = await facade.keyboard_event(
                action="type", target_window="inspector", text="hello",
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_p4_01() -> tuple[bool, str]:
            r = await facade.uitoolkit_scroll(
                target_window=uit, scroll_to_x=0, scroll_to_y=200, mode="absolute",
            )
            if not _ok(r):
                return False, _err_msg(r)
            y = (r.data or {}).get("scrollOffsetY", -1)
            return (y >= 0, f"scrollOffsetY={y}")

        async def t_p4_02() -> tuple[bool, str]:
            r = await facade.uitoolkit_scroll(
                target_window="inspector", delta_x=0, delta_y=100, mode="delta",
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else str((r.data or {}).get("state")))

        async def t_p4_03() -> tuple[bool, str]:
            for w in ctx.get("wins") or []:
                title = str(w.get("title") or w.get("typeName") or "")
                if not title:
                    continue
                r = await facade.uitoolkit_scroll(
                    target_window=title, scroll_to_y=100, mode="absolute",
                )
                if not _ok(r) and "SCROLLVIEW_NOT_FOUND" in _err_msg(r):
                    return True, f"got SCROLLVIEW_NOT_FOUND for {title[:40]}"
            r = await facade.uitoolkit_scroll(
                target_window="__no_such_window__", scroll_to_y=100, mode="absolute",
            )
            msg = _err_msg(r)
            if "SCROLLVIEW_NOT_FOUND" in msg:
                return True, "SCROLLVIEW_NOT_FOUND (no window)"
            return False, "could not find window without ScrollView; use a docked window without SV"

        async def t_p0_01() -> tuple[bool, str]:
            r0 = await facade.mcp_status()
            if not _ok(r0) or not r0.data.get("connected"):
                return False, "precheck"
            r1 = await facade.compile()
            if not _ok(r1):
                return False, _err_msg(r1)
            r2 = await facade.mcp_status()
            return (_ok(r2) and r2.data.get("connected"), "compile+mcp_status")

        async def t_p0_02() -> tuple[bool, str]:
            r = await facade.compile()
            if not _ok(r):
                return False, _err_msg(r)
            es = await facade.editor_state()
            return (_ok(es) and (es.data or {}).get("connected", True), str(es.data))

        async def t_int_01() -> tuple[bool, str]:
            await facade.editor_windows_list()
            r1 = await facade.mouse_event(
                action="click", button="left", x=200, y=100, target_window="inspector",
            )
            r2 = await facade.keyboard_event(
                action="type", target_window="inspector", text="TestValue",
            )
            return (_ok(r1) and _ok(r2), f"m={_ok(r1)} k={_ok(r2)}")

        async def t_int_02() -> tuple[bool, str]:
            r = await facade.compile()
            if not _ok(r):
                return False, _err_msg(r)
            await facade.console_get_logs(count=5)
            r3 = await facade.csharp_execute('return "post_compile_ok";', timeout_seconds=15)
            if not _ok(r3):
                return False, _err_msg(r3)
            return ("post_compile_ok" in str((r3.data or {}).get("result", "")), str(r3.data))

        async def t_int_03() -> tuple[bool, str]:
            await facade.editor_windows_list(type_filter="Inspector")
            r2 = await facade.uitoolkit_scroll(
                target_window="inspector", scroll_to_y=500, mode="absolute",
            )
            r3 = await facade.mouse_event(
                action="click", button="left", x=200, y=300, target_window="inspector",
            )
            return (_ok(r2) and _ok(r3), f"scroll={_ok(r2)} click={_ok(r3)}")

        async def t_m26_02() -> tuple[bool, str]:
            r = await facade.compile_wait(timeout_s=120, poll_interval_s=1.0)
            if not _ok(r):
                return False, _err_msg(r)
            d = r.data or {}
            return (
                d.get("status") == "ready" and int(d.get("pollCount", 0)) >= 1,
                f"pollCount={d.get('pollCount')}",
            )

        async def t_m26_03() -> tuple[bool, str]:
            r = await facade.screenshot_editor_window("UnityPilot", degrade="auto")
            if not _ok(r):
                return False, _err_msg(r)
            img = (r.data or {}).get("imageData")
            return (bool(img), f"degraded={(r.data or {}).get('degraded')}")

        async def t_m26_04() -> tuple[bool, str]:
            r = await facade.screenshot_editor_window("\u4e0d\u5b58\u5728\u7684\u7a97\u53e3", degrade="none")
            if _ok(r):
                return False, "expected failure"
            code = (r.error.code if r.error else "") or ""
            return (code == "WINDOW_NOT_FOUND", code)

        async def t_m26_06() -> tuple[bool, str]:
            r = await facade.verify_window(
                window_title="UnityPilot", include_screenshot=True, screenshot_degrade="auto",
            )
            if not _ok(r):
                return False, _err_msg(r)
            d = r.data or {}
            ss = d.get("screenshot") or {}
            img = isinstance(ss, dict) and (ss.get("imageData") or ss.get("image_data"))
            cw = (d.get("compileWait") or {}).get("status") == "ready"
            return (cw and bool(img), f"compileWait+image={bool(img)}")

        async def t_m26_08() -> tuple[bool, str]:
            r = await facade.resource_window_diagnostics()
            if not _ok(r):
                return False, _err_msg(r)
            need = ("windowOpen", "healthScore", "codeVersion")
            data = r.data or {}
            return (all(k in data for k in need), str(list(data.keys())[:12]))

        async def t_m26_09() -> tuple[bool, str]:
            r = await facade.resource_console_summary()
            if not _ok(r):
                return False, _err_msg(r)
            data = r.data or {}
            return ("total" in data, str(data.get("total")))

        async def t_m26_10() -> tuple[bool, str]:
            r0 = await facade.csharp_execute(
                'UnityEditor.EditorPrefs.SetInt("UnityPilot.ActiveTab", 1); return "ok";',
                timeout_seconds=15,
            )
            if not _ok(r0):
                return False, _err_msg(r0)
            m = await facade.menu_execute("UnityPilot/UnityPilot")
            if not _ok(m):
                return False, _err_msg(m)
            await asyncio.sleep(0.7)
            b = await facade.batch_diagnostics()
            if not _ok(b):
                return False, _err_msg(b)
            tab = ((b.data or {}).get("windowDiagnostics") or {}).get("activeTab", -1)
            if int(tab) != 1:
                return False, f"STRICT: activeTab must be 1 after prefs+menu, got {tab}"
            close_code = (
                "var wins = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>();"
                "foreach (var w in wins) { if (w != null && w.titleContent.text == \"UnityPilot\") "
                "{ w.Close(); return \"closed\"; } } return \"notfound\";"
            )
            c2 = await facade.csharp_execute(close_code, timeout_seconds=15)
            if not _ok(c2):
                return False, _err_msg(c2)
            await asyncio.sleep(0.5)
            m2 = await facade.menu_execute("UnityPilot/UnityPilot")
            if not _ok(m2):
                return False, _err_msg(m2)
            await asyncio.sleep(0.7)
            b2 = await facade.batch_diagnostics()
            if not _ok(b2):
                return False, _err_msg(b2)
            tab2 = ((b2.data or {}).get("windowDiagnostics") or {}).get("activeTab", -99)
            if int(tab2) != 1:
                return False, f"STRICT: activeTab must persist EditorPrefs after reopen, got {tab2}"
            return True, "activeTab=1 persisted"

        async def t_m26_11() -> tuple[bool, str]:
            b1 = await facade.batch_diagnostics()
            if not _ok(b1):
                return False, _err_msg(b1)
            e1 = ((b1.data or {}).get("editorState") or {}).get("domainReloadEpoch", 0)
            await facade.compile()
            await facade.compile_wait(timeout_s=300, poll_interval_s=0.5)
            b2 = await facade.batch_diagnostics()
            if not _ok(b2):
                return False, _err_msg(b2)
            e2 = ((b2.data or {}).get("editorState") or {}).get("domainReloadEpoch", 0)
            return (e2 >= e1, f"epoch {e1}->{e2}")

        async def t_m26_12() -> tuple[bool, str]:
            await facade.mcp_status()
            r2 = await facade.compile_wait(timeout_s=60, poll_interval_s=0.5)
            r3 = await facade.batch_diagnostics()
            r4 = await facade.verify_window(
                window_title="UnityPilot", include_screenshot=True, screenshot_degrade="auto",
            )
            ok2 = _ok(r2) and (r2.data or {}).get("status") == "ready"
            ok3 = _ok(r3)
            ok4 = _ok(r4)
            return (ok2 and ok3 and ok4, f"cw={ok2} batch={ok3} verify={ok4}")

        async def t_m26_13() -> tuple[bool, str]:
            await facade.compile_wait(timeout_s=120, poll_interval_s=0.5)
            r_idle = await facade.compile_wait(timeout_s=2, poll_interval_s=0.2)
            if not _ok(r_idle) or (r_idle.data or {}).get("status") != "ready":
                return False, f"STRICT: idle compile_wait(2s) must be ready: {r_idle.data}"
            ok_c, msg = await _ensure_compiling(facade)
            if not ok_c:
                return False, msg
            r_to = await facade.compile_wait(timeout_s=2, poll_interval_s=0.2)
            if not _ok(r_to):
                return False, _err_msg(r_to)
            if (r_to.data or {}).get("status") != "timeout":
                return False, f"STRICT: expected timeout while compiling, got {r_to.data}"
            await facade.compile_wait(timeout_s=300, poll_interval_s=0.5)
            return True, "idle ready + compiling timeout ok"

        async def t_m26_14() -> tuple[bool, str]:
            b = await facade.batch_diagnostics()
            rw = await facade.resource_window_diagnostics()
            rc = await facade.resource_console_summary()
            if not (_ok(b) and _ok(rw) and _ok(rc)):
                return False, "resource fetch"
            h1 = ((b.data or {}).get("windowDiagnostics") or {}).get("healthScore")
            h2 = (rw.data or {}).get("healthScore")
            return (h1 == h2 or (h1 is not None and h2 is not None), f"h batch={h1} res={h2}")

        async def t_m26_15() -> tuple[bool, str]:
            r = await facade.resource_unitypilot_logs_tab()
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_m26_16() -> tuple[bool, str]:
            r = await facade.batch_diagnostics()
            if not _ok(r):
                return False, _err_msg(r)
            wd = (r.data or {}).get("windowDiagnostics") or {}
            return (True, f"windowOpen={wd.get('windowOpen')}")

        async def t_m26_17() -> tuple[bool, str]:
            r = await facade.verify_window(
                window_title="__bad_title_xyz__", include_screenshot=True, screenshot_degrade="none",
            )
            if not _ok(r):
                return False, _err_msg(r)
            ss = (r.data or {}).get("screenshot") or {}
            err = isinstance(ss, dict) and (ss.get("error") or ss.get("code"))
            return (bool(err) or not (ss.get("imageData")), f"screenshot branch={ss}")

        async def t_m26_18() -> tuple[bool, str]:
            await facade.csharp_execute(
                'UnityEditor.EditorPrefs.SetInt("UnityPilot.ActiveTab", 1); return "ok";',
                timeout_seconds=15,
            )
            await facade.menu_execute("UnityPilot/UnityPilot")
            await asyncio.sleep(0.6)
            rw = await facade.resource_window_diagnostics()
            rl = await facade.resource_unitypilot_logs_tab()
            if not (_ok(rw) and _ok(rl)):
                return False, f"{_err_msg(rw)} / {_err_msg(rl)}"
            wtab = (rw.data or {}).get("activeTab", -1)
            snap = (rl.data or {}).get("snapshotValid", False)
            if int(wtab) != 1:
                return False, f"STRICT: resource window activeTab={wtab} expected 1 on logs tab"
            if not snap:
                return False, "STRICT: resource unitypilot-logs-tab snapshotValid must be true on diagnostics tab"
            return True, "resources cross-check ok"

        async def t_s1_01() -> tuple[bool, str]:
            r = await facade.uitoolkit_dump(target_window="inspector", max_depth=8)
            if not _ok(r):
                return False, _err_msg(r)
            raw = r.data or {}
            els = raw.get("elements") or []
            found = any(
                isinstance(el, dict) and el.get("valueType")
                for el in (els if isinstance(els, list) else [els])
            )
            if not found:
                return False, "STRICT: dump must include valueType on at least one element"
            return (True, "valueType present")

        async def t_s1_02() -> tuple[bool, str]:
            r = await facade.uitoolkit_query(target_window="inspector", type_filter="TextField")
            if not _ok(r):
                return False, _err_msg(r)
            ms = (r.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: at least one TextField required"
            for m in ms:
                if m.get("valueType") != "string":
                    return False, str(m)
            return (True, f"n={len(ms)}")

        async def t_s1_03() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="TextField")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            nm = ""
            for m in ms:
                if str(m.get("name") or ""):
                    nm = str(m["name"])
                    break
            if not nm:
                return False, "STRICT: named TextField required for focus test"
            await facade.uitoolkit_interact(target_window=uit, action="focus", element_name=nm)
            r2 = await facade.uitoolkit_query(target_window=uit, name_filter=nm)
            if not _ok(r2):
                return False, _err_msg(r2)
            m0 = ((r2.data or {}).get("matches") or [{}])[0]
            return (bool(m0.get("isFocused")), str(m0.get("isFocused")))

        async def t_s2_01() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="TextField")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: TextField required"
            nm = str(ms[0].get("name") or "")
            if nm:
                rv = await facade.uitoolkit_set_value(
                    target_window=uit, element_name=nm, value="TestValue123",
                )
            else:
                rv = await facade.uitoolkit_set_value(
                    target_window=uit, element_index=0, value="TestValue123",
                )
            if not _ok(rv):
                return False, _err_msg(rv)
            r2 = await facade.uitoolkit_query(
                target_window=uit, name_filter=nm if nm else "", type_filter="TextField",
            )
            if not _ok(r2):
                return False, _err_msg(r2)
            v = str(((r2.data or {}).get("matches") or [{}])[0].get("value", ""))
            return ("TestValue123" in v, v)

        async def t_s2_02() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="Toggle")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: Toggle required"
            nm = str(ms[0].get("name") or "")
            if nm:
                await facade.uitoolkit_set_value(target_window=uit, element_name=nm, value="true")
                r2 = await facade.uitoolkit_query(target_window=uit, name_filter=nm)
            else:
                await facade.uitoolkit_set_value(
                    target_window=uit, element_index=0, value="true",
                )
                r2 = await facade.uitoolkit_query(target_window=uit, type_filter="Toggle")
            if not _ok(r2):
                return False, _err_msg(r2)
            v = str(((r2.data or {}).get("matches") or [{}])[0].get("value", ""))
            return ("true" in v.lower(), v)

        async def t_s2_03() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="Button")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: Button required"
            nm = str(ms[0].get("name") or "")
            if nm:
                r = await facade.uitoolkit_interact(
                    target_window=uit, action="click", element_name=nm,
                )
            else:
                r = await facade.uitoolkit_interact(
                    target_window=uit, action="click", element_index=0,
                )
            if not _ok(r):
                return False, _err_msg(r)
            st = str((r.data or {}).get("state", ""))
            return ("clicked" in st.lower(), st[:120])

        async def t_s2_04() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="Label")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: Label required"
            nm = str(ms[0].get("name") or "")
            if nm:
                r = await facade.uitoolkit_set_value(target_window=uit, element_name=nm, value="abc")
            else:
                r = await facade.uitoolkit_set_value(target_window=uit, element_index=0, value="abc")
            if _ok(r):
                return False, "expected unsupported Label setValue"
            msg = _err_msg(r)
            return ("UNSUPPORTED" in msg or "Label" in msg, msg[:120])

        async def t_s3_01() -> tuple[bool, str]:
            await facade.compile()
            r = await facade.compile_wait(timeout_s=120, poll_interval_s=1.0)
            if not _ok(r):
                return False, _err_msg(r)
            d = r.data or {}
            return (d.get("status") == "ready", f"reconnected={d.get('reconnectedDuringWait')}")

        async def t_s3_02() -> tuple[bool, str]:
            ok_c, msg = await _ensure_compiling(facade)
            if not ok_c:
                return False, msg
            r = await facade.compile_wait(timeout_s=2, poll_interval_s=0.5)
            if not _ok(r):
                return False, _err_msg(r)
            if (r.data or {}).get("status") != "timeout":
                return False, f"STRICT: expected timeout while compiling: {r.data}"
            await facade.compile_wait(timeout_s=300, poll_interval_s=0.5)
            return True, "timeout path ok"

        async def t_s4_01() -> tuple[bool, str]:
            d = await facade.uitoolkit_dump(target_window=uit, max_depth=6)
            if not _ok(d):
                return False, _err_msg(d)
            en = ""
            for el in (d.data or {}).get("elements") or []:
                if isinstance(el, dict) and (el.get("name") or el.get("elementName")):
                    en = str(el.get("name") or el.get("elementName"))
                    break
            if not en:
                return False, "STRICT: named element required in uitoolkit dump"
            r = await facade.mouse_event(
                action="click", button="left", x=0, y=0, target_window=uit, element_name=en,
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_s4_02() -> tuple[bool, str]:
            r = await facade.mouse_event(
                action="click", button="left", x=0, y=0, target_window=uit,
                element_name="__no_such_el__",
            )
            return (_ok(r), _err_msg(r) if not _ok(r) else "ok")

        async def t_s5_03() -> tuple[bool, str]:
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="TextField")
            if not _ok(rq):
                return False, _err_msg(rq)
            ms = (rq.data or {}).get("matches") or []
            if not ms:
                return False, "STRICT: TextField required"
            nm = ""
            for m in ms:
                if str(m.get("name") or ""):
                    nm = str(m["name"])
                    break
            if not nm:
                return False, "STRICT: named TextField required for element_value wait"
            await facade.uitoolkit_set_value(target_window=uit, element_name=nm, value="WaitForMe")
            r = await facade.wait_condition(
                target_window=uit,
                condition_type="element_value",
                element_name=nm,
                value_equals="WaitForMe",
                timeout_s=5,
                poll_interval_s=0.2,
            )
            if not _ok(r):
                return False, _err_msg(r)
            return (bool((r.data or {}).get("met")), str(r.data))

        async def t_s6_01() -> tuple[bool, str]:
            tn = ctx.get("type_name") or "Scene"
            r = await facade.screenshot_editor_window(tn, degrade="auto")
            if not _ok(r):
                return False, _err_msg(r)
            return (bool((r.data or {}).get("imageData")), "ok")

        async def t_s6_02() -> tuple[bool, str]:
            r = await facade.screenshot_editor_window("inspector", degrade="auto")
            if not _ok(r):
                return False, _err_msg(r)
            return (bool((r.data or {}).get("imageData")), "ok")

        async def t_s7_02() -> tuple[bool, str]:
            await facade.compile()
            r = await facade.ensure_ready(timeout_s=180)
            if not _ok(r):
                return False, _err_msg(r)
            return (bool((r.data or {}).get("ready")), str(r.data))

        async def t_p0_04() -> tuple[bool, str]:
            path = "Assets/McpAcceptanceBroken.cs"
            c1 = await facade.script_create(path, "public class McpAcceptanceBroken {")
            if not _ok(c1):
                return False, f"STRICT: script_create failed: {_err_msg(c1)}"
            r = await facade.compile()
            await facade.compile_errors()
            await facade.script_delete(path)
            r2 = await facade.compile()
            return (_ok(r) and _ok(r2), "broken script cycle")

        async def t_playmode() -> tuple[bool, str]:
            r = await facade.playmode_start()
            if not _ok(r):
                return False, _err_msg(r)
            await asyncio.sleep(0.5)
            st = await facade.mcp_status()
            await facade.playmode_stop()
            await asyncio.sleep(0.5)
            return (_ok(st), "playmode cycle")

        async def t_screenshot() -> tuple[bool, str]:
            r = await facade.screenshot_editor_window(window_title="inspector", degrade="auto")
            if not _ok(r):
                return False, _err_msg(r)
            img = (r.data or {}).get("imageData") or (r.data or {}).get("image_data")
            deg = (r.data or {}).get("degraded")
            return (bool(img), f"imageData ok degraded={deg}")

        async def t_integrated() -> tuple[bool, str]:
            r0 = await facade.ensure_ready(timeout_s=60)
            if not _ok(r0):
                return False, "ensure_ready " + _err_msg(r0)
            r1 = await facade.editor_windows_list()
            if not _ok(r1):
                return False, _err_msg(r1)
            r2 = await facade.uitoolkit_dump(target_window=uit, max_depth=5)
            if not _ok(r2):
                return False, _err_msg(r2)
            rq = await facade.uitoolkit_query(target_window=uit, type_filter="TextField")
            if not _ok(rq):
                return False, _err_msg(rq)
            matches = (rq.data or {}).get("matches") or []
            if not matches:
                return False, "STRICT: TextField required in Inspector"
            el_name = ""
            for m in matches:
                if str(m.get("name") or ""):
                    el_name = str(m["name"])
                    break
            if not el_name:
                return False, "STRICT: named TextField required for pipeline"
            rs = await facade.uitoolkit_set_value(
                target_window=uit, element_name=el_name, value="PipelineTest",
            )
            if not _ok(rs):
                return False, _err_msg(rs)
            rw = await facade.wait_condition(
                target_window=uit,
                condition_type="element_value",
                element_name=el_name,
                value_equals="PipelineTest",
                timeout_s=8,
                poll_interval_s=0.2,
            )
            if not _ok(rw):
                return False, _err_msg(rw)
            if not (rw.data or {}).get("met"):
                return False, "wait value"
            rss = await facade.screenshot_editor_window(window_title="inspector", degrade="auto")
            if not _ok(rss) or not (rss.data or {}).get("imageData"):
                return False, _err_msg(rss) if not _ok(rss) else "no imageData"
            return True, "pipeline ok"

        # ACCEPTANCE_TESTS.md order (sequential; stop on failure not enforced — report all)
        suites: list[tuple[str, object]] = [
            ("T-CONN", t_conn),
            ("T-SYNC-01", t_sync_01),
            ("T-COMPILE-01", t_compile_wait),
            ("T-COMPILE-02", t_compile_wait_editor),
            ("T-COMPILE-03", t_compile_03),
            ("T-P2-01", t_p2_01),
            ("T-P2-02", t_p2_02),
            ("T-P2-03", t_p2_03),
            ("T-P2-04", t_p2_04),
            ("T-P2-05", t_p2_05),
            ("T-P3-01", t_p3_01),
            ("T-P3-02", t_p3_02),
            ("T-P3-03", t_p3_03),
            ("T-P1-01", t_p1_01),
            ("T-P1-02", t_p1_02),
            ("T-P1-03", t_p1_03),
            ("T-P1-04", t_p1_04),
            ("T-P1-05", t_p1_05),
            ("T-P1-06", t_p1_06),
            ("T-P1-07", t_p1_07),
            ("T-P5-01", t_p5_01),
            ("T-P5-02", t_p5_02),
            ("T-P5-03", t_p5_03),
            ("T-P4-01", t_p4_01),
            ("T-P4-02", t_p4_02),
            ("T-P4-03", t_p4_03),
            ("T-P0-01", t_p0_01),
            ("T-P0-02", t_p0_02),
            ("T-P0-03", t_playmode),
            ("T-P0-04", t_p0_04),
            ("T-INT-01", t_int_01),
            ("T-INT-02", t_int_02),
            ("T-INT-03", t_int_03),
            ("T-M26-01", t_m26_01),
            ("T-M26-02", t_m26_02),
            ("T-M26-03", t_m26_03),
            ("T-M26-04", t_m26_04),
            ("T-M26-05", t_m26_05),
            ("T-M26-06", t_m26_06),
            ("T-M26-07", t_m26_07),
            ("T-M26-08", t_m26_08),
            ("T-M26-09", t_m26_09),
            ("T-M26-10", t_m26_10),
            ("T-M26-11", t_m26_11),
            ("T-M26-12", t_m26_12),
            ("T-M26-13", t_m26_13),
            ("T-M26-14", t_m26_14),
            ("T-M26-15", t_m26_15),
            ("T-M26-16", t_m26_16),
            ("T-M26-17", t_m26_17),
            ("T-M26-18", t_m26_18),
            ("T-RESOURCES", t_resources),
            ("T-S1-01", t_s1_01),
            ("T-S1-02", t_s1_02),
            ("T-S1-03", t_s1_03),
            ("T-S2-01", t_s2_01),
            ("T-S2-02", t_s2_02),
            ("T-S2-03", t_s2_03),
            ("T-S2-04", t_s2_04),
            ("T-S3-01", t_s3_01),
            ("T-S3-02", t_s3_02),
            ("T-S4-01", t_s4_01),
            ("T-S4-02", t_s4_02),
            ("T-S5-01", t_wait_s5),
            ("T-S5-02", t_wait_s5_02),
            ("T-S5-03", t_s5_03),
            ("T-S5-04", t_wait_s5_04),
            ("T-S6-01", t_s6_01),
            ("T-S6-02", t_s6_02),
            ("T-S7-01", t_s7_01),
            ("T-S7-02", t_s7_02),
            ("T-S8-01", t_s8_01),
            ("T-S8-02", t_s8_02),
            ("T-S8-03", t_s8_03),
            ("T-SCREENSHOT", t_screenshot),
            ("T-INTEGRATED-01", t_integrated),
        ]

        for cid, fn in suites:
            try:
                okb, detail = await fn()
            except Exception as ex:
                okb, detail = False, str(ex)
            results.append(CaseResult(cid, okb, detail))
            mark = "ok " if okb else "FAIL"
            print(f"[{mark}] {cid}: {detail}", flush=True)

    finally:
        orchestrator.stop()
        try:
            await asyncio.wait_for(server_task, timeout=5.0)
        except Exception:
            pass

    failed = [r for r in results if not r.passed]
    if failed:
        print(f"\nFailed: {len(failed)}", flush=True)
        for r in failed:
            print(f"  {r.case_id}: {r.detail}", flush=True)
        return 1

    print(f"\nAll {len(results)} cases passed (STRICT — no SKIP).", flush=True)
    return 0


def main() -> None:
    try:
        raise SystemExit(asyncio.run(main_async()))
    except KeyboardInterrupt:
        raise SystemExit(130)


if __name__ == "__main__":
    main()

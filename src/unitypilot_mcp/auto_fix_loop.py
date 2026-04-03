from __future__ import annotations

import asyncio
from dataclasses import asdict, dataclass, field
from typing import Any

from .dispatcher import CommandDispatcher
from .fix_planner import CompileFixPlanner
from .patch_service import PatchApplyService
from .protocol import new_id, now_ms
from .state_store import StateStore


@dataclass(slots=True)
class AutoFixSnapshot:
    loop_id: str
    status: str
    max_iterations: int
    current_iteration: int
    stop_when_no_error: bool
    last_compile_error_count: int
    fixed_files: list[str] = field(default_factory=list)
    reports: list[dict[str, Any]] = field(default_factory=list)
    started_at: int = field(default_factory=now_ms)
    finished_at: int = 0
    last_error: str = ""


class AutoFixLoopService:
    def __init__(self, dispatcher: CommandDispatcher, state: StateStore, patch_service: PatchApplyService, planner: CompileFixPlanner) -> None:
        self.dispatcher = dispatcher
        self.state = state
        self.patch_service = patch_service
        self.planner = planner
        self._task: asyncio.Task | None = None
        self._stop_requested = False

    def status(self) -> AutoFixSnapshot | None:
        return self.state.auto_fix

    async def start(self, max_iterations: int, stop_when_no_error: bool) -> AutoFixSnapshot:
        running = self.state.auto_fix
        if running and running.status == "running":
            return running

        loop = AutoFixSnapshot(
            loop_id=new_id("loop"),
            status="running",
            max_iterations=max(1, min(50, max_iterations)),
            current_iteration=0,
            stop_when_no_error=stop_when_no_error,
            last_compile_error_count=self.state.compile.error_count,
        )
        self.state.auto_fix = loop
        self._stop_requested = False
        self._task = asyncio.create_task(self._run(loop))
        return loop

    def stop(self, loop_id: str) -> AutoFixSnapshot | None:
        loop = self.state.auto_fix
        if not loop or loop.loop_id != loop_id:
            return None

        self._stop_requested = True
        return loop

    async def _run(self, loop: AutoFixSnapshot) -> None:
        for idx in range(1, loop.max_iterations + 1):
            if self._stop_requested:
                loop.status = "failed"
                loop.last_error = "手动停止"
                loop.finished_at = now_ms()
                return

            loop.current_iteration = idx
            compile_request_id = new_id("compile")
            resp = await self.dispatcher.call(
                request_id=new_id("req"),
                name="compile.request",
                payload={"requestId": compile_request_id},
                timeout_ms=120000,
            )
            if not resp.ok:
                loop.status = "failed"
                loop.last_error = resp.error.message if resp.error else "compile failed"
                loop.finished_at = now_ms()
                return

            errors = list(self.state.compile.errors)
            loop.last_compile_error_count = len(errors)

            if loop.last_compile_error_count == 0 and loop.stop_when_no_error:
                loop.status = "success"
                loop.finished_at = now_ms()
                return

            planned = self.planner.plan(errors, max_files=20)
            if not planned:
                loop.status = "failed"
                loop.last_error = "未能规划出可执行补丁"
                loop.finished_at = now_ms()
                return

            planned_reports = [item.to_report() for item in planned]
            patch_results = self.patch_service.apply_batch([item.request for item in planned])

            success_files = [item.get("filePath", "") for item in patch_results if item.get("writeStatus") == "success"]
            loop.fixed_files.extend([f for f in success_files if f])

            # 每轮报告：规划 + 落盘结果
            for report_item, patch_item in zip(planned_reports, patch_results):
                merged = {
                    "iteration": idx,
                    **report_item,
                    "writeStatus": patch_item.get("writeStatus", "failed"),
                    "errorMessage": patch_item.get("errorMessage", ""),
                }
                loop.reports.append(merged)

            if not success_files:
                loop.status = "failed"
                loop.last_error = "补丁执行失败"
                loop.finished_at = now_ms()
                return

        loop.status = "max_reached"
        loop.finished_at = now_ms()

    def as_dict(self) -> dict[str, Any] | None:
        loop = self.state.auto_fix
        return asdict(loop) if loop else None

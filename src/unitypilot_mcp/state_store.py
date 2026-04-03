from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(slots=True)
class CommandRecord:
    command_id: str
    request_id: str
    name: str
    payload: dict[str, Any]
    status: str = "pending"
    result: dict[str, Any] | None = None
    error: dict[str, Any] | None = None


@dataclass(slots=True)
class CompileSnapshot:
    status: str = "idle"
    compile_request_id: str = ""
    error_count: int = 0
    warning_count: int = 0
    started_at: int = 0
    finished_at: int = 0
    errors: list[dict[str, Any]] = field(default_factory=list)


@dataclass(slots=True)
class EditorSnapshot:
    connected: bool = False
    is_compiling: bool = False
    play_mode_state: str = "edit"
    active_scene: str = ""


class StateStore:
    def __init__(self) -> None:
        self.commands: dict[str, CommandRecord] = {}
        self.compile = CompileSnapshot()
        self.editor = EditorSnapshot()
        self.auto_fix = None

    def create_command(self, command_id: str, request_id: str, name: str, payload: dict[str, Any]) -> CommandRecord:
        record = CommandRecord(
            command_id=command_id,
            request_id=request_id,
            name=name,
            payload=payload,
            status="sent",
        )
        self.commands[command_id] = record
        return record

    def mark_success(self, command_id: str, payload: dict[str, Any]) -> None:
        cmd = self.commands.get(command_id)
        if not cmd:
            return
        cmd.status = "success"
        cmd.result = payload

    def mark_failed(self, command_id: str, error: dict[str, Any]) -> None:
        cmd = self.commands.get(command_id)
        if not cmd:
            return
        cmd.status = "failed"
        cmd.error = error

    def update_compile_status(self, payload: dict[str, Any]) -> None:
        self.compile.compile_request_id = str(payload.get("requestId", self.compile.compile_request_id))
        status = str(payload.get("status", self.compile.status))
        self.compile.status = "compiling" if status in ("started", "in_progress", "compiling") else "finished"
        self.compile.error_count = int(payload.get("errorCount", self.compile.error_count))
        self.compile.warning_count = int(payload.get("warningCount", self.compile.warning_count))
        self.compile.started_at = int(payload.get("startedAt", self.compile.started_at))
        self.compile.finished_at = int(payload.get("finishedAt", self.compile.finished_at))

    def update_compile_errors(self, payload: dict[str, Any]) -> None:
        errors = payload.get("errors") or []
        self.compile.errors = list(errors)
        self.compile.error_count = int(payload.get("total", len(self.compile.errors)))

    def update_editor_state(self, payload: dict[str, Any]) -> None:
        self.editor.connected = bool(payload.get("connected", self.editor.connected))
        self.editor.is_compiling = bool(payload.get("isCompiling", self.editor.is_compiling))
        self.editor.play_mode_state = str(payload.get("playModeState", self.editor.play_mode_state))
        self.editor.active_scene = str(payload.get("activeScene", self.editor.active_scene))

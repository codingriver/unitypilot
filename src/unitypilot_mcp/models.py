from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Any


@dataclass(slots=True)
class WsMessage:
    id: str
    type: str
    name: str
    payload: dict[str, Any]
    timestamp: int
    session_id: str
    protocol_version: str = "1.0"


@dataclass(slots=True)
class SessionState:
    session_id: str
    unity_version: str = ""
    project_path: str = ""
    platform: str = ""
    connected: bool = False
    authenticated: bool = False
    last_heartbeat_at: int = 0


@dataclass(slots=True)
class CompileErrorItem:
    file: str
    line: int
    column: int
    message: str
    severity: str = "error"


@dataclass(slots=True)
class ToolError:
    code: str
    message: str
    detail: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class ToolResponse:
    ok: bool
    data: dict[str, Any] | None
    error: ToolError | None
    request_id: str
    timestamp: int = field(default_factory=lambda: int(time.time() * 1000))

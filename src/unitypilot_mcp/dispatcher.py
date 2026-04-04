from __future__ import annotations

import asyncio
from typing import Any

from .models import ToolResponse
from .protocol import new_id
from .responses import fail, ok
from .state_store import StateStore


class CommandDispatcher:
    DEFAULT_TIMEOUT_MS = 30000
    COMMAND_TIMEOUT_MS: dict[str, int] = {
        "compile.request": 180000,
        "compile.wait": 660000,
        "compile.errors.get": 45000,
        "test.run": 300000,
        "build.start": 600000,
    }
    PREFIX_TIMEOUT_MS: dict[str, int] = {
        "package.": 120000,
        "scene.": 45000,
        "script.": 60000,
    }

    def __init__(self, transport: "WsTransport", state: StateStore, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> None:
        self.transport = transport
        self.state = state
        self.timeout_ms = timeout_ms

    def _resolve_timeout_ms(self, name: str, timeout_ms: int | None) -> int:
        if timeout_ms is not None:
            return timeout_ms
        if name in self.COMMAND_TIMEOUT_MS:
            return self.COMMAND_TIMEOUT_MS[name]
        for prefix, value in self.PREFIX_TIMEOUT_MS.items():
            if name.startswith(prefix):
                return value
        return self.timeout_ms

    def timeout_policy_snapshot(self) -> dict[str, Any]:
        return {
            "default": self.timeout_ms,
            "commands": dict(self.COMMAND_TIMEOUT_MS),
            "prefixes": dict(self.PREFIX_TIMEOUT_MS),
        }

    async def call(self, request_id: str, name: str, payload: dict[str, Any], timeout_ms: int | None = None) -> ToolResponse:
        if not self.transport.is_ready():
            return fail(request_id, "UNITY_NOT_CONNECTED", "Unity 未连接", {"command": name})

        command_id = new_id("cmd")
        self.state.create_command(command_id=command_id, request_id=request_id, name=name, payload=payload)

        future = self.transport.register_pending(command_id)
        await self.transport.send_command(command_id=command_id, name=name, payload=payload)

        wait_timeout = self._resolve_timeout_ms(name, timeout_ms) / 1000
        try:
            result = await asyncio.wait_for(future, timeout=wait_timeout)
        except asyncio.TimeoutError:
            self.state.mark_failed(command_id, {"code": "COMMAND_TIMEOUT", "message": "命令超时"})
            return fail(request_id, "COMMAND_TIMEOUT", "命令超时", {"command": name, "commandId": command_id})

        if result.get("type") == "error":
            err = result.get("payload") or {}
            self.state.mark_failed(command_id, err)
            return fail(
                request_id,
                str(err.get("code", "INTERNAL_ERROR")),
                str(err.get("message", "命令执行失败")),
                {"command": name, "commandId": command_id, "detail": err.get("detail", {})},
            )

        payload_data = result.get("payload") or {}
        self.state.mark_success(command_id, payload_data)
        return ok(request_id, payload_data)


class WsTransport:
    def is_ready(self) -> bool:
        raise NotImplementedError

    def register_pending(self, command_id: str) -> asyncio.Future:
        raise NotImplementedError

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        raise NotImplementedError

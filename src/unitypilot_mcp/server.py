from __future__ import annotations

import asyncio
import json
import logging
from typing import Any

import websockets
from websockets.server import WebSocketServerProtocol

from .dispatcher import WsTransport
from .protocol import PROTOCOL_VERSION, from_wire, now_ms, to_wire
from .session_manager import SessionManager
from .state_store import CompileSnapshot, StateStore

logger = logging.getLogger("unitypilot.server")


class WsOrchestratorServer(WsTransport):
    def __init__(self, host: str = "127.0.0.1", port: int = 8765, heartbeat_interval_ms: int = 2000) -> None:
        self.host = host
        self.port = port
        self.heartbeat_interval_ms = heartbeat_interval_ms
        self.session_manager = SessionManager(heartbeat_timeout_ms=heartbeat_interval_ms * 3)
        self.state = StateStore()
        self._ws: WebSocketServerProtocol | None = None
        self._pending: dict[str, asyncio.Future] = {}
        self._server = None
        self._stop_event = asyncio.Event()

    async def start(self) -> None:
        self._stop_event.clear()
        self._server = await websockets.serve(self._handle, self.host, self.port)
        logger.info("WebSocket server listening on %s:%s", self.host, self.port)
        try:
            await self._stop_event.wait()
        finally:
            logger.info("WebSocket server shutting down on %s:%s", self.host, self.port)
            if self._server is not None:
                self._server.close()
                await self._server.wait_closed()
                self._server = None
            self._ws = None
            self.session_manager.disconnect()
            self._fail_all_pending("SERVER_STOPPED", "MCP 服务器已关闭")
            logger.info("WebSocket server stopped")

    def stop(self) -> None:
        self._stop_event.set()

    def is_ready(self) -> bool:
        return self._ws is not None and self.session_manager.is_connected()

    def register_pending(self, command_id: str) -> asyncio.Future:
        loop = asyncio.get_running_loop()
        fut = loop.create_future()
        self._pending[command_id] = fut
        return fut

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        if not self._ws or not self.session_manager.active:
            return
        session_id = self.session_manager.active.session_id
        logger.info("[%s] >>> %s  cmd=%s", session_id[:12], name, command_id[:16])
        msg = {
            "id": command_id,
            "type": "command",
            "name": name,
            "payload": payload,
            "timestamp": now_ms(),
            "sessionId": session_id,
            "protocolVersion": PROTOCOL_VERSION,
        }
        raw = json.dumps(msg, ensure_ascii=False)
        logger.debug("[%s] >>> SEND %s", session_id[:12], raw)
        await self._ws.send(raw)

    def _fail_all_pending(self, code: str, message: str) -> None:
        """Resolve all pending futures with a connection-lost error so callers fail fast."""
        for fut in list(self._pending.values()):
            if not fut.done():
                fut.set_result({
                    "id": "",
                    "type": "error",
                    "name": "connection_lost",
                    "payload": {"code": code, "message": message},
                    "timestamp": now_ms(),
                    "sessionId": "",
                    "protocolVersion": PROTOCOL_VERSION,
                })
        self._pending.clear()

    async def _handle(self, websocket: WebSocketServerProtocol) -> None:
        remote = websocket.remote_address
        logger.info("Unity client connected from %s", remote)
        self._ws = websocket
        heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        try:
            async for raw in websocket:
                incoming = from_wire(json.loads(raw))
                if incoming.type != "heartbeat":
                    logger.debug("[recv] %s", raw)
                await self._handle_message(incoming)
        finally:
            heartbeat_task.cancel()
            self._ws = None
            session_id = self.session_manager.active.session_id if self.session_manager.active else "unknown"
            logger.info("[%s] Unity client disconnected from %s", session_id[:12], remote)
            self.session_manager.disconnect()
            self._fail_all_pending("CONNECTION_LOST", "Unity 连接断开")
            # Mark compile state unreliable on disconnect
            if self.state.compile.status == "compiling":
                self.state.compile.status = "unknown"
                self.state.compile.errors = []

    async def _handle_message(self, message) -> None:
        if message.type == "hello" and message.name == "session.hello":
            # Fail pending commands from any previous session before accepting new one
            if self._pending:
                self._fail_all_pending("SESSION_REPLACED", "Unity 会话已替换，请重试命令")
            # Reset compile state — stale snapshot from previous session is unreliable
            self.state.compile = CompileSnapshot()
            self.session_manager.on_hello(message.session_id, message.payload)
            logger.info(
                "[%s] Session established  unity=%s  project=%s  platform=%s",
                message.session_id[:12],
                message.payload.get("unityVersion", "?"),
                message.payload.get("projectPath", "?"),
                message.payload.get("platform", "?"),
            )
            ack = {
                "id": message.id,
                "type": "result",
                "name": "session.hello",
                "payload": {"accepted": True, "heartbeatIntervalMs": self.heartbeat_interval_ms},
                "timestamp": now_ms(),
                "sessionId": message.session_id,
                "protocolVersion": PROTOCOL_VERSION,
            }
            if self._ws:
                ack_raw = json.dumps(ack, ensure_ascii=False)
                logger.debug("[%s] >>> SEND hello_ack %s", message.session_id[:12], ack_raw)
                await self._ws.send(ack_raw)
            return

        if message.type == "heartbeat":
            self.session_manager.on_heartbeat(message.session_id)
            return

        if message.type in ("result", "error"):
            fut = self._pending.pop(message.id, None)
            if fut and not fut.done():
                fut.set_result(to_wire(message))
            log_fn = logger.info if message.type == "result" else logger.warning
            log_fn(
                "[%s] <<< %s  type=%s  cmd=%s",
                message.session_id[:12] if message.session_id else "?",
                message.name,
                message.type,
                message.id[:16] if message.id else "?",
            )
            return

        if message.type == "event":
            logger.debug(
                "[%s] <<< EVENT %s  payload=%s",
                message.session_id[:12] if message.session_id else "?",
                message.name,
                json.dumps(message.payload, ensure_ascii=False) if message.payload else "{}",
            )
            if message.name == "compile.status":
                self.state.update_compile_status(message.payload)
            elif message.name == "compile.errors":
                self.state.update_compile_errors(message.payload)
            elif message.name == "editor.state":
                self.state.update_editor_state(message.payload)
            elif message.name == "playmode.changed":
                self.state.editor.play_mode_state = str(message.payload.get("state", self.state.editor.play_mode_state))

    async def _heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(self.heartbeat_interval_ms / 1000)
            if not self._ws or not self.session_manager.active:
                continue
            hb = {
                "id": f"hb-{now_ms()}",
                "type": "heartbeat",
                "name": "session.heartbeat",
                "payload": {},
                "timestamp": now_ms(),
                "sessionId": self.session_manager.active.session_id,
                "protocolVersion": PROTOCOL_VERSION,
            }
            hb_raw = json.dumps(hb, ensure_ascii=False)
            await self._ws.send(hb_raw)

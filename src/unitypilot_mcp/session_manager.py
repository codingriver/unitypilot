from __future__ import annotations

from .models import SessionState
from .protocol import now_ms


class SessionManager:
    def __init__(self, heartbeat_timeout_ms: int = 6000) -> None:
        self._active: SessionState | None = None
        self.heartbeat_timeout_ms = heartbeat_timeout_ms

    @property
    def active(self) -> SessionState | None:
        return self._active

    def on_hello(self, session_id: str, payload: dict) -> SessionState:
        self._active = SessionState(
            session_id=session_id,
            unity_version=str(payload.get("unityVersion", "")),
            project_path=str(payload.get("projectPath", "")),
            platform=str(payload.get("platform", "")),
            connected=True,
            authenticated=True,
            last_heartbeat_at=now_ms(),
        )
        return self._active

    def on_heartbeat(self, session_id: str) -> None:
        if not self._active or self._active.session_id != session_id:
            return
        self._active.last_heartbeat_at = now_ms()
        self._active.connected = True

    def is_connected(self) -> bool:
        if not self._active:
            return False
        if now_ms() - self._active.last_heartbeat_at > self.heartbeat_timeout_ms:
            self._active.connected = False
        return self._active.connected

    def disconnect(self, session_id: str | None = None, *, force: bool = False) -> None:
        """Mark session disconnected.

        - ``force=True`` (server shutdown): always clear *connected*.
        - ``session_id`` set: only clear when it matches *active* (stale socket after reconnect).
        - ``session_id`` is None and not force: no-op (socket died before ``session.hello``).
        """
        if not self._active:
            return
        if force:
            self._active.connected = False
            return
        if session_id is None:
            return
        if self._active.session_id != session_id:
            return
        self._active.connected = False

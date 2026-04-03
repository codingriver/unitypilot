from __future__ import annotations

import uuid
import time
from typing import Any

from .models import WsMessage


PROTOCOL_VERSION = "1.0"


def now_ms() -> int:
    return int(time.time() * 1000)


def new_id(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4()}"


def to_wire(msg: WsMessage) -> dict[str, Any]:
    return {
        "id": msg.id,
        "type": msg.type,
        "name": msg.name,
        "payload": msg.payload,
        "timestamp": msg.timestamp,
        "sessionId": msg.session_id,
        "protocolVersion": msg.protocol_version,
    }


def from_wire(data: dict[str, Any]) -> WsMessage:
    return WsMessage(
        id=str(data.get("id", "")),
        type=str(data.get("type", "")),
        name=str(data.get("name", "")),
        payload=data.get("payload") or {},
        timestamp=int(data.get("timestamp") or 0),
        session_id=str(data.get("sessionId", "")),
        protocol_version=str(data.get("protocolVersion", "")),
    )

from __future__ import annotations

from .models import ToolError, ToolResponse
from .protocol import now_ms


def ok(request_id: str, data: dict) -> ToolResponse:
    return ToolResponse(ok=True, data=data, error=None, request_id=request_id, timestamp=now_ms())


def fail(request_id: str, code: str, message: str, detail: dict | None = None) -> ToolResponse:
    return ToolResponse(
        ok=False,
        data=None,
        error=ToolError(code=code, message=message, detail=detail or {}),
        request_id=request_id,
        timestamp=now_ms(),
    )

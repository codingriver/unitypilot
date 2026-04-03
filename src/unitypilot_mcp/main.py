from __future__ import annotations

import asyncio

from .server import WsOrchestratorServer


async def main() -> None:
    server = WsOrchestratorServer(host="127.0.0.1", port=8765)
    print("[UnityPilot MCP] WS server listening at ws://127.0.0.1:8765")
    await server.start()


if __name__ == "__main__":
    asyncio.run(main())

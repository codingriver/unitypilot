from __future__ import annotations

import asyncio
import logging
import os
import socket
import sys
import time
from pathlib import Path


MCP_ROOT = Path(__file__).resolve().parent
SRC_ROOT = MCP_ROOT / "src"

MAX_PORT_RETRIES = 5
RETRY_INTERVAL_SEC = 5


def _resolve_host_port() -> tuple[str, int]:
    """Resolve host/port from CLI args or env vars (same logic as mcp_stdio_server)."""
    host = os.environ.get("UNITYPILOT_HOST", "127.0.0.1")
    port_str = os.environ.get("UNITYPILOT_PORT", "8765")

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif args[i] == "--port" and i + 1 < len(args):
            port_str = args[i + 1]
            i += 2
        else:
            i += 1

    try:
        port = int(port_str)
    except ValueError:
        port = 8765

    return host, port


def _is_port_occupied(host: str, port: int) -> bool:
    """Return True when the port is already in use."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.3)
        return sock.connect_ex((host, port)) == 0


def _resolve_log_file() -> str | None:
    """Resolve log file path from CLI args (--log-file) or env var (UNITYPILOT_LOG_FILE)."""
    log_file = os.environ.get("UNITYPILOT_LOG_FILE", "")

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--log-file" and i + 1 < len(args):
            log_file = args[i + 1]
            i += 2
        else:
            i += 1

    return log_file or None


def _resolve_log_level() -> str:
    """Resolve log level from CLI args (--log-level) or env var (UNITYPILOT_LOG_LEVEL).

    Default: DEBUG (all logs).
    Valid values: DEBUG, INFO, WARNING, ERROR, CRITICAL
    """
    level = os.environ.get("UNITYPILOT_LOG_LEVEL", "DEBUG")

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--log-level" and i + 1 < len(args):
            level = args[i + 1]
            i += 2
        else:
            i += 1

    return level.upper()


_VALID_LOG_LEVELS = {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}


def _setup_logging(log_file: str | None = None, log_level: str = "DEBUG") -> None:
    """Configure unitypilot logging.

    - stderr: uses log_level
    - file (if specified): uses log_level
    - Default log_level is DEBUG — all communication data included.
    """
    if log_level not in _VALID_LOG_LEVELS:
        log_level = "DEBUG"

    numeric_level = getattr(logging, log_level)

    fmt = logging.Formatter(
        "[%(asctime)s] %(name)s %(levelname)s  %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    root = logging.getLogger("unitypilot")
    root.setLevel(numeric_level)

    # stderr handler
    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setLevel(numeric_level)
    stderr_handler.setFormatter(fmt)
    root.addHandler(stderr_handler)

    # File handler (flush immediately — no buffering)
    if log_file:
        file_handler = logging.FileHandler(log_file, encoding="utf-8")
        file_handler.setLevel(numeric_level)
        file_handler.setFormatter(fmt)
        file_handler.stream.reconfigure(write_through=True)
        root.addHandler(file_handler)


if str(SRC_ROOT) not in sys.path:
    sys.path.insert(0, str(SRC_ROOT))

from unitypilot_mcp.mcp_main import main


if __name__ == "__main__":
    log_file = _resolve_log_file()
    log_level = _resolve_log_level()
    _setup_logging(log_file, log_level)
    logger = logging.getLogger("unitypilot.startup")

    host, port = _resolve_host_port()

    # Retry if port is occupied
    for attempt in range(1, MAX_PORT_RETRIES + 1):
        if not _is_port_occupied(host, port):
            break
        if attempt == MAX_PORT_RETRIES:
            logger.error(
                "Port %s:%s still occupied after %d retries (%ds interval). Exiting.",
                host, port, MAX_PORT_RETRIES, RETRY_INTERVAL_SEC,
            )
            raise SystemExit(1)
        logger.warning(
            "Port %s:%s is occupied, retry %d/%d in %ds...",
            host, port, attempt, MAX_PORT_RETRIES, RETRY_INTERVAL_SEC,
        )
        time.sleep(RETRY_INTERVAL_SEC)

    logger.info("Starting unitypilot MCP server on %s:%s", host, port)
    asyncio.run(main())

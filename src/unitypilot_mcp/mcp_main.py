from __future__ import annotations

import asyncio
import logging
import os
import sys

from .mcp_stdio_server import main


def _setup_logging() -> None:
    """Configure logging for unitypilot when used as a package entry point."""
    root = logging.getLogger("unitypilot")
    if root.handlers:
        return

    log_level = os.environ.get("UNITYPILOT_LOG_LEVEL", "DEBUG").upper()
    if log_level not in {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}:
        log_level = "DEBUG"
    numeric_level = getattr(logging, log_level)

    fmt = logging.Formatter(
        "[%(asctime)s] %(name)s %(levelname)s  %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    root.setLevel(numeric_level)

    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setLevel(numeric_level)
    stderr_handler.setFormatter(fmt)
    root.addHandler(stderr_handler)

    log_file = os.environ.get("UNITYPILOT_LOG_FILE", "")
    if log_file:
        file_handler = logging.FileHandler(log_file, encoding="utf-8")
        file_handler.setLevel(numeric_level)
        file_handler.setFormatter(fmt)
        file_handler.stream.reconfigure(write_through=True)
        root.addHandler(file_handler)


def _cli() -> None:
    """Console script entry point (used by pip install / uvx)."""
    _setup_logging()
    asyncio.run(main())


if __name__ == "__main__":
    _cli()

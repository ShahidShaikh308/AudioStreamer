"""Configure the Windows node's bounded rotating log once at application startup."""

from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler

from platforms.windows.desktop.config import PROJECT_ROOT


def configure_logging(level: str) -> None:
    log_directory = PROJECT_ROOT / "logs"
    log_directory.mkdir(exist_ok=True)
    formatter = logging.Formatter(
        "%(asctime)s %(levelname)s %(name)s event=%(message)s"
    )
    console = logging.StreamHandler()
    console.setFormatter(formatter)
    file_handler = RotatingFileHandler(
        log_directory / "audiostreamer.log",
        maxBytes=1_000_000,
        backupCount=3,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    logging.basicConfig(
        level=getattr(logging, level.upper()),
        handlers=(console, file_handler),
        force=True,
    )

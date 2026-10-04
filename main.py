"""Direct entry point for the AudioStreamer desktop application."""

from __future__ import annotations

import logging

from platforms.windows.desktop.bootstrap import create_windows_node
from platforms.windows.desktop.config import WindowsNodeConfig
from platforms.windows.desktop.gui import launch_gui
from platforms.windows.desktop.logging_config import configure_logging


def main() -> int:
    try:
        config = WindowsNodeConfig.load()
        configure_logging(config.log_level)
        node = create_windows_node(config)
        logging.getLogger("application").info("AudioStreamer Windows node starting")
        launch_gui(node)
    except Exception:
        logging.basicConfig(level=logging.ERROR)
        logging.getLogger("application").exception("AudioStreamer GUI could not start")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

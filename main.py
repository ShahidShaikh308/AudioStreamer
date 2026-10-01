"""Direct entry point for the AudioStreamer desktop application."""

from __future__ import annotations

import logging

from server.gui import launch_gui


def main() -> int:
    try:
        launch_gui()
    except Exception:
        logging.basicConfig(level=logging.ERROR)
        logging.getLogger("application").exception("AudioStreamer GUI could not start")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""Direct entry point for the AudioStreamer desktop application."""

from __future__ import annotations

import logging
import tkinter as tk
from tkinter import messagebox

from control_plane.diagnostics import format_startup_error
from control_plane.transport import ControlTransportStartupError
from platforms.windows.desktop.bootstrap import create_windows_node
from platforms.windows.desktop.config import WindowsNodeConfig
from platforms.windows.desktop.gui import launch_gui
from platforms.windows.desktop.logging_config import configure_logging


def main() -> int:
    config: WindowsNodeConfig | None = None
    try:
        config = WindowsNodeConfig.load()
        configure_logging(config.log_level)
        node = create_windows_node(config)
        logging.getLogger("application").info("AudioStreamer Windows node starting")
        launch_gui(node)
    except Exception as error:
        logging.basicConfig(level=logging.ERROR)
        logging.getLogger("application").exception("AudioStreamer GUI could not start")
        if isinstance(error, ControlTransportStartupError):
            try:
                root = tk.Tk()
                root.withdraw()
                messagebox.showerror(
                    "AudioStreamer Control Plane startup failed",
                    format_startup_error(
                        config.control_port if config is not None else 55050,
                        str(error),
                    ),
                    parent=root,
                )
                root.destroy()
            except Exception:
                print(
                    format_startup_error(
                        config.control_port if config is not None else 55050,
                        str(error),
                    )
                )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

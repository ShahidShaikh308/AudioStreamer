"""Standalone Windows Device Manager entry point."""

from .app import launch_device_manager
from platforms.windows.desktop.logging_config import configure_logging


if __name__ == "__main__":
    configure_logging("INFO")
    launch_device_manager()

"""Shared settings for the Windows Control Plane broker."""

from __future__ import annotations

import tomllib
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = PROJECT_ROOT / "config" / "config.local.toml"
DEFAULT_CONTROL_PORT = 55050


def load_control_port(path: Path = CONFIG_PATH) -> int:
    if not path.exists():
        return DEFAULT_CONTROL_PORT
    with path.open("rb") as config_file:
        value = tomllib.load(config_file).get("control_port", DEFAULT_CONTROL_PORT)
    if type(value) is not int or not 49152 <= value <= 65535:
        raise ValueError("control_port must be between 49152 and 65535")
    return value

"""Persistent preferences for the standalone Control Plane desktop client."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path


def default_settings_path() -> Path:
    base = Path(os.environ.get("APPDATA", Path.home()))
    return base / "AudioStreamer" / "device_manager.json"


@dataclass(frozen=True, slots=True)
class DeviceManagerSettings:
    node_name: str = "AudioStreamer Device Manager"
    control_port: int = 55050
    receive_port: int = 5007

    @classmethod
    def load(cls, path: Path | None = None) -> DeviceManagerSettings:
        target = path or default_settings_path()
        if not target.exists():
            return cls()
        values = json.loads(target.read_text(encoding="utf-8"))
        settings = cls(**values)
        settings.validate()
        return settings

    def save(self, path: Path | None = None) -> None:
        self.validate()
        target = path or default_settings_path()
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps({
            "node_name": self.node_name,
            "control_port": self.control_port,
            "receive_port": self.receive_port,
        }, indent=2) + "\n", encoding="utf-8")

    def validate(self) -> None:
        if not self.node_name.strip() or len(self.node_name.encode("utf-8")) > 255:
            raise ValueError("Node name must contain 1..255 UTF-8 bytes")
        if not 49152 <= self.control_port <= 65535:
            raise ValueError("Control port must be between 49152 and 65535")
        if not 1 <= self.receive_port <= 65535:
            raise ValueError("Receive port must be between 1 and 65535")

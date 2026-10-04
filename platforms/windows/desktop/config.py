"""Load and validate persistent configuration for the Windows audio node.

The existing receiver_ip/receiver_port TOML keys are retained as the node's
outbound peer destination so an existing user configuration keeps its target.
"""

from __future__ import annotations

import json
import socket
from dataclasses import dataclass
from ipaddress import AddressValueError, IPv4Address
from pathlib import Path

import tomllib

PROJECT_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_CONFIG_PATH = PROJECT_ROOT / "config" / "config.toml"
UDP_PORT = 5005  # Default peer destination port; Android currently listens here.


@dataclass(frozen=True, slots=True)
class WindowsNodeConfig:
    device_name: str = socket.gethostname()
    receiver_ip: str = "127.0.0.1"
    receiver_port: int = UDP_PORT
    receive_port: int = 5006
    audio_device_index: int = -1
    capture_queue_chunks: int = 8
    frames_per_buffer: int = 480
    log_level: str = "INFO"

    @classmethod
    def load(cls, path: Path = DEFAULT_CONFIG_PATH) -> WindowsNodeConfig:
        """Load TOML values, creating a default file when it is missing."""
        if not path.exists():
            config = cls()
            config.save(path)
            return config
        with path.open("rb") as config_file:
            values = tomllib.load(config_file)
        allowed = {field for field in cls.__dataclass_fields__}
        unknown = values.keys() - allowed
        if unknown:
            raise ValueError(
                f"Unknown configuration key(s): {', '.join(sorted(unknown))}"
            )
        config = cls(**values)
        config.validate()
        return config

    def save(self, path: Path = DEFAULT_CONFIG_PATH) -> None:
        """Write user-editable settings as a small, readable TOML file."""
        self.validate()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            self._to_toml(),
            encoding="utf-8",
        )

    def _to_toml(self) -> str:
        return (
            "# AudioStreamer Windows node settings.\n"
            f"device_name = {json.dumps(self.device_name.strip())}\n"
            f"receiver_ip = {json.dumps(self.receiver_ip.strip())}\n"
            f"receiver_port = {self.receiver_port}\n"
            f"receive_port = {self.receive_port}\n"
            f"audio_device_index = {self.audio_device_index}\n"
            f"capture_queue_chunks = {self.capture_queue_chunks}\n"
            f"frames_per_buffer = {self.frames_per_buffer}\n"
            f"log_level = {json.dumps(self.log_level.upper())}\n"
        )

    def validate(self) -> None:
        if (
            not isinstance(self.device_name, str)
            or not self.device_name.strip()
            or len(self.device_name) > 128
            or any(character in self.device_name for character in "\r\n\0")
        ):
            raise ValueError(
                "device_name must be a non-empty single-line name (max 128 characters)"
            )
        if not isinstance(self.receiver_ip, str) or not self.receiver_ip.strip():
            raise ValueError("receiver_ip must be a non-empty string")
        try:
            IPv4Address(self.receiver_ip.strip())
        except AddressValueError as error:
            raise ValueError("receiver_ip must be a valid IPv4 address") from error
        if type(self.receiver_port) is not int or not 1 <= self.receiver_port <= 65535:
            raise ValueError("receiver_port must be between 1 and 65535")
        if type(self.receive_port) is not int or not 1 <= self.receive_port <= 65535:
            raise ValueError("receive_port must be between 1 and 65535")
        if type(self.audio_device_index) is not int or self.audio_device_index < -1:
            raise ValueError("audio_device_index must be -1 (default) or a device index")
        if type(self.capture_queue_chunks) is not int or self.capture_queue_chunks < 1:
            raise ValueError("capture_queue_chunks must be at least 1")
        if type(self.frames_per_buffer) is not int or self.frames_per_buffer < 1:
            raise ValueError("frames_per_buffer must be at least 1")
        if not isinstance(self.log_level, str) or self.log_level.upper() not in {
            "DEBUG",
            "INFO",
            "WARNING",
            "ERROR",
            "CRITICAL",
        }:
            raise ValueError(
                "log_level must be DEBUG, INFO, WARNING, ERROR, or CRITICAL"
            )

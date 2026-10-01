"""Load and validate the user-editable server configuration."""

from __future__ import annotations

from dataclasses import dataclass
from ipaddress import AddressValueError, IPv4Address
from pathlib import Path

import tomllib

PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG_PATH = PROJECT_ROOT / "config" / "config.toml"
UDP_PORT = 5005  # Must match the Android receiver's fixed listening port.


@dataclass(frozen=True, slots=True)
class ServerConfig:
    receiver_ip: str = "127.0.0.1"
    receiver_port: int = UDP_PORT
    protocol_version: int = 2
    capture_queue_chunks: int = 8
    frames_per_buffer: int = 480
    log_level: str = "INFO"

    @classmethod
    def load(cls, path: Path = DEFAULT_CONFIG_PATH) -> ServerConfig:
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
            "# AudioStreamer server settings. Restart streaming after edits.\n"
            f'receiver_ip = "{self.receiver_ip.strip()}"\n'
            f"receiver_port = {self.receiver_port}\n"
            f"protocol_version = {self.protocol_version}\n"
            f"capture_queue_chunks = {self.capture_queue_chunks}\n"
            f"frames_per_buffer = {self.frames_per_buffer}\n"
            f'log_level = "{self.log_level.upper()}"\n',
            encoding="utf-8",
        )

    def validate(self) -> None:
        if not isinstance(self.receiver_ip, str) or not self.receiver_ip.strip():
            raise ValueError("receiver_ip must be a non-empty string")
        try:
            IPv4Address(self.receiver_ip.strip())
        except AddressValueError as error:
            raise ValueError("receiver_ip must be a valid IPv4 address") from error
        if type(self.receiver_port) is not int or not 1 <= self.receiver_port <= 65535:
            raise ValueError("receiver_port must be between 1 and 65535")
        if type(self.protocol_version) is not int or self.protocol_version not in {
            1,
            2,
        }:
            raise ValueError("protocol_version must be 1 or 2")
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

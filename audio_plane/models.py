"""Small, platform-neutral PCM value types shared across audio-plane modules."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class PcmFormat:
    sample_rate: int
    channels: int
    sample_width_bytes: int = 4
    encoding: str = "float32"


@dataclass(frozen=True, slots=True)
class PcmChunk:
    """Interleaved little-endian float32 PCM and its frame count."""

    data: bytes
    frames: int

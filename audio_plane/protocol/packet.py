"""Strongly typed model for one active-version AudioStreamer PCM packet."""

from __future__ import annotations

from dataclasses import dataclass

from .enums import SampleFormat


class PacketError(ValueError):
    """Base error for malformed or unrepresentable protocol packets."""


@dataclass(frozen=True, slots=True)
class AudioPacket:
    sequence_number: int
    stream_id: int
    sample_rate: int
    channels: int
    sample_format: SampleFormat
    payload: bytes

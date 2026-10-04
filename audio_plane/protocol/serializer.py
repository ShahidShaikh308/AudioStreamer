"""Serialize protocol packets without performing network I/O."""

from __future__ import annotations

import struct

from .constants import (
    HEADER,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    MAX_PAYLOAD_SIZE,
    PROTOCOL_VERSION,
    SAMPLE_WIDTH_BYTES,
)
from .enums import SampleFormat
from .packet import AudioPacket, PacketError


class SerializationError(PacketError):
    """Raised when a packet cannot be represented by the active wire format."""


def _uint(value: int, bits: int, name: str) -> int:
    if type(value) is not int or not 0 <= value < (1 << bits):
        raise SerializationError(f"{name} must be an unsigned {bits}-bit integer")
    return value


def serialize_packet(packet: AudioPacket) -> bytes:
    if packet.sample_format != SampleFormat.FLOAT32_LE:
        raise SerializationError("protocol v3 supports FLOAT32_LE only")
    if not 1 <= packet.sample_rate <= 0x7FFF_FFFF:
        raise SerializationError("sample rate must fit a positive signed 32-bit value")
    if packet.channels != 2:
        raise SerializationError("protocol v3 currently supports stereo only")
    if not packet.payload:
        raise SerializationError("audio payload must not be empty")
    if len(packet.payload) % (packet.channels * SAMPLE_WIDTH_BYTES):
        raise SerializationError("payload must contain complete interleaved frames")

    payload_length = _uint(len(packet.payload), 16, "payload length")
    if payload_length > MAX_PAYLOAD_SIZE or HEADER.size + payload_length > MAX_DATAGRAM_SIZE:
        raise SerializationError("packet exceeds the 1200-byte datagram limit")

    try:
        header = HEADER.pack(
            MAGIC,
            PROTOCOL_VERSION,
            _uint(packet.sequence_number, 32, "sequence number"),
            _uint(packet.stream_id, 64, "stream ID"),
            _uint(packet.sample_rate, 32, "sample rate"),
            _uint(packet.channels, 8, "channel count"),
            int(packet.sample_format),
            payload_length,
        )
    except (struct.error, OverflowError) as exc:
        raise SerializationError(str(exc)) from exc

    if packet.stream_id == 0:
        raise SerializationError("stream ID zero is reserved")
    return header + packet.payload

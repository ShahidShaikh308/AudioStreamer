"""Compatibility facade for protocol serialization and parsing.

New code may import models and operations from ``packet``, ``serializer``, and
``parser`` directly. This module retains the original project's names so its
UDP sender can keep using the compact serialization API.
"""

from __future__ import annotations

from collections.abc import Iterator

from .constants import (
    CURRENT_VERSION,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    MAX_PAYLOAD_SIZE,
    SAMPLE_WIDTH_BYTES,
    UINT32_MASK,
)
from .constants import (
    V2_BASE_HEADER as HEADER,
)
from .constants import (
    V2_FIXED_HEADER_SIZE as HEADER_SIZE,
)
from .enums import PacketFlags, PacketType, ProtocolVersion, SampleFormat
from .packet import AudioPacket, PacketError
from .parser import decode_packet, parse_packet
from .serializer import SerializationError, encode_packet, serialize_packet

VERSION = CURRENT_VERSION
SAMPLE_FORMAT_FLOAT32_LE = int(SampleFormat.FLOAT32_LE)


def split_pcm_payload(payload: bytes, channels: int) -> list[bytes]:
    """Compatibility helper returning all frame-aligned PCM payloads as a list."""
    return list(iter_pcm_payloads(payload, channels))


def iter_pcm_payloads(payload: bytes, channels: int) -> Iterator[bytes]:
    """Yield frame-aligned PCM slices within the v2 MTU limit, one at a time."""
    if channels < 1:
        raise PacketError("channel count must be positive")
    if not payload:
        return []
    frame_size = channels * SAMPLE_WIDTH_BYTES
    if len(payload) % frame_size:
        raise PacketError("PCM buffer must contain whole interleaved audio frames")
    packet_payload_size = (MAX_PAYLOAD_SIZE // frame_size) * frame_size
    if packet_payload_size == 0:
        raise PacketError("channel count leaves no room for an audio frame")
    for offset in range(0, len(payload), packet_payload_size):
        yield payload[offset : offset + packet_payload_size]


__all__ = [
    "HEADER",
    "HEADER_SIZE",
    "MAGIC",
    "MAX_DATAGRAM_SIZE",
    "MAX_PAYLOAD_SIZE",
    "SAMPLE_FORMAT_FLOAT32_LE",
    "SAMPLE_WIDTH_BYTES",
    "UINT32_MASK",
    "VERSION",
    "AudioPacket",
    "PacketError",
    "PacketFlags",
    "PacketType",
    "ProtocolVersion",
    "SampleFormat",
    "SerializationError",
    "decode_packet",
    "encode_packet",
    "iter_pcm_payloads",
    "parse_packet",
    "serialize_packet",
    "split_pcm_payload",
]

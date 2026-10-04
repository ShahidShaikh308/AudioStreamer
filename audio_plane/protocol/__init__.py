"""Public protocol API and frame-aligned PCM payload packetization."""

from __future__ import annotations

from collections.abc import Iterator

from .constants import (
    HEADER,
    HEADER_SIZE,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    MAX_PAYLOAD_SIZE,
    PROTOCOL_VERSION,
    SAMPLE_WIDTH_BYTES,
    UINT32_MASK,
)
from .enums import SampleFormat
from .packet import AudioPacket, PacketError
from .parser import parse_packet
from .serializer import SerializationError, serialize_packet


def iter_pcm_payloads(payload: bytes, channels: int) -> Iterator[bytes]:
    """Yield MTU-safe PCM slices without splitting an interleaved frame."""
    if channels != 2:
        raise PacketError("protocol v3 currently supports stereo only")
    frame_size = channels * SAMPLE_WIDTH_BYTES
    if len(payload) % frame_size:
        raise PacketError("PCM buffer must contain whole interleaved audio frames")
    packet_size = (MAX_PAYLOAD_SIZE // frame_size) * frame_size
    for offset in range(0, len(payload), packet_size):
        yield payload[offset : offset + packet_size]


__all__ = [
    "HEADER",
    "HEADER_SIZE",
    "MAGIC",
    "MAX_DATAGRAM_SIZE",
    "MAX_PAYLOAD_SIZE",
    "PROTOCOL_VERSION",
    "SAMPLE_WIDTH_BYTES",
    "UINT32_MASK",
    "AudioPacket",
    "PacketError",
    "SampleFormat",
    "SerializationError",
    "iter_pcm_payloads",
    "parse_packet",
    "serialize_packet",
]

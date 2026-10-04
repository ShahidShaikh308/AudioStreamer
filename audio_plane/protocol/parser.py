"""Parse and validate active-version protocol datagrams without socket I/O."""

from __future__ import annotations

from .constants import (
    HEADER,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    PROTOCOL_VERSION,
    SAMPLE_WIDTH_BYTES,
)
from .enums import SampleFormat
from .packet import AudioPacket, PacketError


def parse_packet(datagram: bytes) -> AudioPacket:
    if not isinstance(datagram, bytes):
        raise TypeError("datagram must be bytes")
    if len(datagram) > MAX_DATAGRAM_SIZE:
        raise PacketError("datagram exceeds the 1200-byte maximum")
    if len(datagram) < HEADER.size:
        raise PacketError(f"datagram is shorter than the {HEADER.size}-byte header")

    magic, version, sequence, stream_id, sample_rate, channels, sample_format, payload_length = (
        HEADER.unpack_from(datagram)
    )
    if magic != MAGIC:
        raise PacketError("invalid protocol magic")
    if version != PROTOCOL_VERSION:
        raise PacketError(f"unsupported protocol version: {version}")
    if stream_id == 0:
        raise PacketError("stream ID zero is reserved")
    if not 1 <= sample_rate <= 0x7FFF_FFFF:
        raise PacketError("sample rate must fit a positive signed 32-bit value")
    if channels != 2:
        raise PacketError("protocol v3 currently supports stereo only")
    if sample_format != int(SampleFormat.FLOAT32_LE):
        raise PacketError(f"unsupported sample format: {sample_format}")
    if payload_length != len(datagram) - HEADER.size:
        raise PacketError("payload length does not match datagram size")

    payload = datagram[HEADER.size:]
    if not payload:
        raise PacketError("audio payload must not be empty")
    if len(payload) % (channels * SAMPLE_WIDTH_BYTES):
        raise PacketError("payload does not contain complete interleaved frames")

    return AudioPacket(
        sequence_number=sequence,
        stream_id=stream_id,
        sample_rate=sample_rate,
        channels=channels,
        sample_format=SampleFormat.FLOAT32_LE,
        payload=payload,
    )

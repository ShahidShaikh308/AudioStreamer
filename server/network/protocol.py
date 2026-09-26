"""Versioned UDP packet format for float32 PCM audio.

All header fields use network byte order. The payload is interleaved,
little-endian float32 PCM, matching ``WasapiLoopbackCapture``. ``timestamp_ns``
is the sender's monotonic clock in nanoseconds; it is useful for relative timing
but is not directly comparable with the receiver's clock.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass

MAGIC = b"ASAU"
VERSION = 1
SAMPLE_FORMAT_FLOAT32_LE = 1
HEADER = struct.Struct("!4sBBHIQIHH")
HEADER_SIZE = HEADER.size
MAX_DATAGRAM_SIZE = 1200
MAX_PAYLOAD_SIZE = MAX_DATAGRAM_SIZE - HEADER_SIZE
SAMPLE_WIDTH_BYTES = 4
UINT32_MASK = 0xFFFF_FFFF


class PacketError(ValueError):
    """Raised when a packet is malformed or contains unsupported audio."""


@dataclass(frozen=True)
class AudioPacket:
    sequence_number: int
    timestamp_ns: int
    sample_rate: int
    channels: int
    payload: bytes


def _validate_fields(packet: AudioPacket) -> None:
    if not 0 <= packet.sequence_number <= UINT32_MASK:
        raise PacketError("sequence number must fit in an unsigned 32-bit integer")
    if not 0 <= packet.timestamp_ns <= 0xFFFF_FFFF_FFFF_FFFF:
        raise PacketError("timestamp must fit in an unsigned 64-bit integer")
    if not 1 <= packet.sample_rate <= 0xFFFF_FFFF:
        raise PacketError("sample rate must be a positive unsigned 32-bit integer")
    if not 1 <= packet.channels <= 0xFFFF:
        raise PacketError("channel count must be a positive unsigned 16-bit integer")
    if not packet.payload:
        raise PacketError("PCM payload must not be empty")
    if len(packet.payload) > MAX_PAYLOAD_SIZE:
        raise PacketError(f"payload exceeds {MAX_PAYLOAD_SIZE} bytes")
    frame_size = packet.channels * SAMPLE_WIDTH_BYTES
    if len(packet.payload) % frame_size:
        raise PacketError("payload length must contain whole interleaved audio frames")


def encode_packet(packet: AudioPacket) -> bytes:
    """Serialize one validated audio packet to a UDP datagram."""
    _validate_fields(packet)
    header = HEADER.pack(
        MAGIC,
        VERSION,
        SAMPLE_FORMAT_FLOAT32_LE,
        0,
        packet.sequence_number,
        packet.timestamp_ns,
        packet.sample_rate,
        packet.channels,
        len(packet.payload),
    )
    return header + packet.payload


def decode_packet(datagram: bytes) -> AudioPacket:
    """Validate a complete datagram and return its PCM packet."""
    if len(datagram) < HEADER_SIZE:
        raise PacketError("datagram is shorter than the protocol header")
    if len(datagram) > MAX_DATAGRAM_SIZE:
        raise PacketError("datagram exceeds the configured maximum size")

    (
        magic,
        version,
        sample_format,
        flags,
        sequence_number,
        timestamp_ns,
        sample_rate,
        channels,
        payload_length,
    ) = HEADER.unpack_from(datagram)

    if magic != MAGIC:
        raise PacketError("invalid packet magic")
    if version != VERSION:
        raise PacketError(f"unsupported protocol version: {version}")
    if sample_format != SAMPLE_FORMAT_FLOAT32_LE:
        raise PacketError(f"unsupported sample format: {sample_format}")
    if flags != 0:
        raise PacketError("reserved flags must be zero")
    if payload_length != len(datagram) - HEADER_SIZE:
        raise PacketError("declared payload length does not match datagram size")

    packet = AudioPacket(
        sequence_number=sequence_number,
        timestamp_ns=timestamp_ns,
        sample_rate=sample_rate,
        channels=channels,
        payload=datagram[HEADER_SIZE:],
    )
    _validate_fields(packet)
    return packet


def split_pcm_payload(payload: bytes, channels: int) -> list[bytes]:
    """Split PCM into whole-frame payloads that fit in a conservative UDP MTU."""
    if channels < 1:
        raise PacketError("channel count must be positive")
    if not payload:
        return []
    frame_size = channels * SAMPLE_WIDTH_BYTES
    if len(payload) % frame_size:
        raise PacketError("PCM buffer must contain whole interleaved audio frames")

    bytes_per_packet = (MAX_PAYLOAD_SIZE // frame_size) * frame_size
    if bytes_per_packet == 0:
        raise PacketError("channel count leaves no room for an audio frame")
    return [
        payload[offset : offset + bytes_per_packet]
        for offset in range(0, len(payload), bytes_per_packet)
    ]

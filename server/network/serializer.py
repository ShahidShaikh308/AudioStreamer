"""Serialize protocol packets without performing any network I/O."""

from __future__ import annotations

import struct

from .constants import (
    CURRENT_VERSION,
    LEGACY_VERSION,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    V1_HEADER,
    V2_BASE_HEADER,
    V2_FIXED_HEADER_SIZE,
)
from .enums import PacketType, ProtocolVersion, SAMPLE_FORMAT_WIDTH_BYTES, SampleFormat
from .packet import AudioPacket, PacketError


class SerializationError(PacketError):
    """Raised when a packet cannot be represented on the wire."""


def _uint(value: int, bits: int, name: str) -> int:
    if not isinstance(value, int) or not 0 <= value < (1 << bits):
        raise SerializationError(f"{name} must be an unsigned {bits}-bit integer")
    return value


def _check_audio(packet: AudioPacket) -> None:
    if not packet.payload:
        raise SerializationError("audio packets must contain a nonempty payload")
    if packet.sample_rate <= 0 or packet.channels <= 0:
        raise SerializationError("audio packets require a positive rate and channel count")
    if int(packet.sample_format) == int(SampleFormat.UNKNOWN):
        raise SerializationError("audio packets must declare a sample format")
    try:
        sample_format = SampleFormat(packet.sample_format)
        width = SAMPLE_FORMAT_WIDTH_BYTES[sample_format]
    except (ValueError, KeyError):
        # Unknown future sample formats can be forwarded by version-aware relays.
        return
    if len(packet.payload) % (packet.channels * width):
        raise SerializationError("audio payload must contain whole interleaved frames")


def _validate_extensions(extensions: bytes) -> None:
    """Validate the v2 TLV framing while leaving extension meanings opaque."""
    offset = 0
    while offset < len(extensions):
        if len(extensions) - offset < 4:
            raise SerializationError("truncated extension TLV header")
        extension_type = int.from_bytes(extensions[offset : offset + 2], "big")
        value_length = int.from_bytes(extensions[offset + 2 : offset + 4], "big")
        if extension_type == 0:
            raise SerializationError("extension type zero is reserved")
        raw_length = 4 + value_length
        padded_length = (raw_length + 3) & ~3
        end = offset + padded_length
        if end > len(extensions):
            raise SerializationError("extension TLV exceeds the declared header length")
        padding = extensions[offset + raw_length : end]
        if any(padding):
            raise SerializationError("extension padding bytes must be zero")
        offset = end


def serialize_packet(packet: AudioPacket) -> bytes:
    """Encode a packet as a v1-compatible or v2 UDP payload."""
    version = int(packet.protocol_version)
    if version == LEGACY_VERSION:
        return _serialize_v1(packet)
    if version == CURRENT_VERSION:
        return _serialize_v2(packet)
    raise SerializationError(f"unsupported protocol version: {version}")


def _serialize_v1(packet: AudioPacket) -> bytes:
    if int(packet.packet_type) != int(PacketType.AUDIO):
        raise SerializationError("legacy v1 supports audio packets only")
    if int(packet.flags) != 0 or packet.extensions:
        raise SerializationError("legacy v1 does not support flags or extensions")
    if int(packet.sample_format) != int(SampleFormat.FLOAT32_LE):
        raise SerializationError("legacy v1 supports float32 little-endian only")
    _check_audio(packet)

    payload_length = _uint(len(packet.payload), 16, "payload length")
    if payload_length > MAX_DATAGRAM_SIZE - V1_HEADER.size:
        raise SerializationError("v1 payload exceeds the maximum datagram size")
    try:
        header = V1_HEADER.pack(
            MAGIC,
            LEGACY_VERSION,
            int(packet.sample_format),
            0,
            _uint(packet.sequence_number, 32, "sequence number"),
            _uint(packet.timestamp_ns, 64, "timestamp"),
            _uint(packet.sample_rate, 32, "sample rate"),
            _uint(packet.channels, 16, "channel count"),
            payload_length,
        )
    except (struct.error, OverflowError) as exc:
        raise SerializationError(str(exc)) from exc
    return header + packet.payload


def _serialize_v2(packet: AudioPacket) -> bytes:
    packet_type = _uint(int(packet.packet_type), 8, "packet type")
    sample_format = _uint(int(packet.sample_format), 8, "sample format")
    flags = _uint(int(packet.flags), 16, "flags")
    sequence_number = _uint(packet.sequence_number, 32, "sequence number")
    stream_id = _uint(packet.stream_id, 64, "stream ID")
    if stream_id == 0:
        raise SerializationError("v2 stream ID zero is reserved")
    timestamp_ns = _uint(packet.timestamp_ns, 64, "timestamp")
    sample_rate = _uint(packet.sample_rate, 32, "sample rate")
    channels = _uint(packet.channels, 16, "channel count")
    payload_length = _uint(len(packet.payload), 16, "payload length")

    if packet_type == int(PacketType.AUDIO):
        _check_audio(packet)
    elif packet_type in (int(PacketType.CONTROL), int(PacketType.KEEPALIVE)):
        if sample_rate != 0 or channels != 0 or sample_format != int(SampleFormat.UNKNOWN):
            raise SerializationError("non-audio packets must set audio format fields to zero")
        if packet_type == int(PacketType.KEEPALIVE) and packet.payload:
            raise SerializationError("keepalive packets must have an empty payload")

    _validate_extensions(packet.extensions)
    header_length = V2_FIXED_HEADER_SIZE + len(packet.extensions)
    if header_length > 0xFFFF or header_length + payload_length > MAX_DATAGRAM_SIZE:
        raise SerializationError("header and payload exceed the maximum datagram size")
    try:
        header = V2_BASE_HEADER.pack(
            MAGIC,
            CURRENT_VERSION,
            packet_type,
            header_length,
            flags,
            sequence_number,
            stream_id,
            timestamp_ns,
            sample_rate,
            channels,
            sample_format,
            0,  # reserved; nonzero values are invalid in v2
            payload_length,
        )
    except (struct.error, OverflowError) as exc:
        raise SerializationError(str(exc)) from exc
    return header + packet.extensions + packet.payload


# Keep the older API name available to current server and receiver code.
encode_packet = serialize_packet

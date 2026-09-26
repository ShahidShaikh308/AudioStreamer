"""Parse and validate protocol datagrams without opening sockets."""

from __future__ import annotations

from .constants import (
    CURRENT_VERSION,
    LEGACY_VERSION,
    MAGIC,
    MAX_DATAGRAM_SIZE,
    V1_HEADER,
    V2_BASE_HEADER,
    V2_FIXED_HEADER_SIZE,
)
from .enums import PacketFlags, PacketType, ProtocolVersion, SAMPLE_FORMAT_WIDTH_BYTES, SampleFormat
from .packet import AudioPacket, PacketError


def _enum_or_integer(enum_type, value: int):
    try:
        return enum_type(value)
    except ValueError:
        return value


def _validate_audio(packet: AudioPacket) -> None:
    if packet.sample_rate < 1 or packet.channels < 1:
        raise PacketError("audio packet requires positive sample rate and channel count")
    if not packet.payload:
        raise PacketError("audio packet payload must not be empty")
    if int(packet.sample_format) == int(SampleFormat.UNKNOWN):
        raise PacketError("audio packet must declare a supported sample format")
    try:
        width = SAMPLE_FORMAT_WIDTH_BYTES[SampleFormat(packet.sample_format)]
    except (ValueError, KeyError):
        # Unknown future encodings remain parseable; an endpoint that cannot
        # decode them can discard the packet based on its format identifier.
        return
    if len(packet.payload) % (packet.channels * width):
        raise PacketError("audio payload does not contain whole interleaved frames")


def _validate_extensions(extensions: bytes) -> None:
    offset = 0
    while offset < len(extensions):
        if len(extensions) - offset < 4:
            raise PacketError("truncated extension TLV header")
        extension_type = int.from_bytes(extensions[offset : offset + 2], "big")
        value_length = int.from_bytes(extensions[offset + 2 : offset + 4], "big")
        if extension_type == 0:
            raise PacketError("extension type zero is reserved")
        raw_length = 4 + value_length
        padded_length = (raw_length + 3) & ~3
        end = offset + padded_length
        if end > len(extensions):
            raise PacketError("extension TLV exceeds declared header length")
        if any(extensions[offset + raw_length : end]):
            raise PacketError("extension padding bytes must be zero")
        offset = end


def parse_packet(datagram: bytes, *, allow_legacy: bool = True) -> AudioPacket:
    """Parse a complete v2 datagram or, optionally, the existing v1 audio wire format."""
    if not isinstance(datagram, bytes):
        raise TypeError("datagram must be bytes")
    if len(datagram) > MAX_DATAGRAM_SIZE:
        raise PacketError("datagram exceeds the 1200-byte maximum")
    if len(datagram) < 5:
        raise PacketError("datagram is too short to contain protocol identification")

    magic = datagram[:4]
    version = datagram[4]
    if magic != MAGIC:
        raise PacketError("invalid protocol magic")
    if version == LEGACY_VERSION:
        if not allow_legacy:
            raise PacketError("legacy v1 packets are disabled")
        return _parse_v1(datagram)
    if version == CURRENT_VERSION:
        return _parse_v2(datagram)
    raise PacketError(f"unsupported protocol version: {version}")


def _parse_v1(datagram: bytes) -> AudioPacket:
    if len(datagram) < V1_HEADER.size:
        raise PacketError("v1 datagram is shorter than its 28-byte header")
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
    ) = V1_HEADER.unpack_from(datagram)
    if magic != MAGIC or version != LEGACY_VERSION:
        raise PacketError("invalid v1 header")
    if sample_format != int(SampleFormat.FLOAT32_LE):
        raise PacketError("legacy v1 only supports float32 little-endian audio")
    if flags != 0:
        raise PacketError("legacy v1 reserved field must be zero")
    if payload_length != len(datagram) - V1_HEADER.size:
        raise PacketError("v1 payload length does not match datagram size")

    packet = AudioPacket(
        sequence_number=sequence_number,
        timestamp_ns=timestamp_ns,
        sample_rate=sample_rate,
        channels=channels,
        payload=datagram[V1_HEADER.size :],
        stream_id=0,
        packet_type=PacketType.AUDIO,
        flags=PacketFlags.NONE,
        sample_format=SampleFormat.FLOAT32_LE,
        protocol_version=ProtocolVersion.LEGACY_V1,
    )
    _validate_audio(packet)
    return packet


def _parse_v2(datagram: bytes) -> AudioPacket:
    if len(datagram) < V2_FIXED_HEADER_SIZE:
        raise PacketError(
            f"v2 datagram is shorter than the {V2_FIXED_HEADER_SIZE}-byte base header"
        )
    (
        magic,
        version,
        packet_type,
        header_length,
        flags,
        sequence_number,
        stream_id,
        timestamp_ns,
        sample_rate,
        channels,
        sample_format,
        reserved,
        payload_length,
    ) = V2_BASE_HEADER.unpack_from(datagram)

    if magic != MAGIC or version != CURRENT_VERSION:
        raise PacketError("invalid v2 header")
    if header_length < V2_FIXED_HEADER_SIZE:
        raise PacketError("header length is shorter than the fixed v2 header")
    if header_length > len(datagram):
        raise PacketError("header length exceeds datagram size")
    if reserved != 0:
        raise PacketError("reserved header byte must be zero")
    if stream_id == 0:
        raise PacketError("v2 stream ID zero is reserved")
    if payload_length != len(datagram) - header_length:
        raise PacketError("payload length does not match datagram size")

    extensions = datagram[V2_FIXED_HEADER_SIZE:header_length]
    _validate_extensions(extensions)
    packet = AudioPacket(
        sequence_number=sequence_number,
        timestamp_ns=timestamp_ns,
        sample_rate=sample_rate,
        channels=channels,
        payload=datagram[header_length:],
        stream_id=stream_id,
        packet_type=_enum_or_integer(PacketType, packet_type),
        flags=PacketFlags(flags),
        sample_format=_enum_or_integer(SampleFormat, sample_format),
        protocol_version=ProtocolVersion.CURRENT,
        extensions=extensions,
    )

    if packet_type == int(PacketType.AUDIO):
        _validate_audio(packet)
    elif packet_type in (int(PacketType.CONTROL), int(PacketType.KEEPALIVE)):
        if sample_rate != 0 or channels != 0 or sample_format != int(SampleFormat.UNKNOWN):
            raise PacketError("non-audio packet contains nonzero audio format fields")
        if packet_type == int(PacketType.KEEPALIVE) and payload_length != 0:
            raise PacketError("keepalive packet payload must be empty")
    return packet


# Compatibility function name used by the previous protocol module.
decode_packet = parse_packet

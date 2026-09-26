"""Strongly typed packet model shared by serializer, parser, and transports."""

from __future__ import annotations

from dataclasses import dataclass

from .enums import PacketFlags, PacketType, ProtocolVersion, SampleFormat


class PacketError(ValueError):
    """Base error for malformed or unrepresentable protocol packets."""


@dataclass(frozen=True, slots=True)
class AudioPacket:
    """Decoded or to-be-serialized packet.

    Unknown packet-type or sample-format identifiers are retained as integers,
    allowing an implementation to skip packet kinds it does not understand.
    ``extensions`` contains the raw v2 TLV extension area.
    """

    sequence_number: int
    timestamp_ns: int
    sample_rate: int
    channels: int
    payload: bytes
    stream_id: int = 1
    packet_type: PacketType | int = PacketType.AUDIO
    flags: PacketFlags | int = PacketFlags.NONE
    sample_format: SampleFormat | int = SampleFormat.FLOAT32_LE
    protocol_version: ProtocolVersion | int = ProtocolVersion.CURRENT
    extensions: bytes = b""

    def __post_init__(self) -> None:
        # Normalize recognized identifiers but preserve future unknown values.
        for field_name, enum_type in (
            ("packet_type", PacketType),
            ("sample_format", SampleFormat),
            ("protocol_version", ProtocolVersion),
        ):
            value = getattr(self, field_name)
            try:
                object.__setattr__(self, field_name, enum_type(value))
            except ValueError:
                if not isinstance(value, int):
                    raise TypeError(f"{field_name} must be an integer wire identifier")
        try:
            object.__setattr__(self, "flags", PacketFlags(self.flags))
        except ValueError as exc:
            raise TypeError("flags must be an integer bit field") from exc
        if not isinstance(self.payload, bytes):
            raise TypeError("payload must be bytes")
        if not isinstance(self.extensions, bytes):
            raise TypeError("extensions must be bytes")

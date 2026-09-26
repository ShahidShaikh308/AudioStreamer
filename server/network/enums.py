"""Wire-level enumerations for protocol version 2."""

from enum import IntEnum, IntFlag


class ProtocolVersion(IntEnum):
    LEGACY_V1 = 1
    CURRENT = 2


class PacketType(IntEnum):
    AUDIO = 1
    CONTROL = 2
    KEEPALIVE = 3


class PacketFlags(IntFlag):
    NONE = 0
    DISCONTINUITY = 1 << 0
    END_OF_STREAM = 1 << 1


class SampleFormat(IntEnum):
    """Sample encodings; enum values are stable wire identifiers."""

    UNKNOWN = 0  # Used by non-audio packets.
    FLOAT32_LE = 1
    PCM_S16_LE = 2
    PCM_S24_LE = 3
    PCM_S32_LE = 4


SAMPLE_FORMAT_WIDTH_BYTES = {
    SampleFormat.FLOAT32_LE: 4,
    SampleFormat.PCM_S16_LE: 2,
    SampleFormat.PCM_S24_LE: 3,
    SampleFormat.PCM_S32_LE: 4,
}

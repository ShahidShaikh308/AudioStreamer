"""Shared immutable wire constants."""

import struct


MAGIC = b"ASAU"
CURRENT_VERSION = 2
LEGACY_VERSION = 1

# V1: magic, version, float32 format, zero flags, seq, timestamp, rate,
# channels, payload length. This is the existing project's original layout.
V1_HEADER = struct.Struct("!4sBBHIQIHH")

# V2 fixed base: magic, version, type, header bytes, flags, sequence, stream
# ID, timestamp, sample rate, channels, sample format, reserved, payload bytes.
V2_BASE_HEADER = struct.Struct("!4sBBHHIQQIHBBH")

MAX_DATAGRAM_SIZE = 1200
MAX_V1_PAYLOAD_SIZE = MAX_DATAGRAM_SIZE - V1_HEADER.size
MAX_V2_PAYLOAD_SIZE = MAX_DATAGRAM_SIZE - V2_BASE_HEADER.size
MAX_PAYLOAD_SIZE = MAX_V2_PAYLOAD_SIZE
MAX_HEADER_SIZE = MAX_DATAGRAM_SIZE

V2_FIXED_HEADER_SIZE = V2_BASE_HEADER.size
SAMPLE_WIDTH_BYTES = 4
UINT32_MASK = 0xFFFF_FFFF

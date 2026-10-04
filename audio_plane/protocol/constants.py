"""Wire constants for the active protocol documented in the root PROTOCOL.md."""

import struct


MAGIC = b"ASAU"
PROTOCOL_VERSION = 3
HEADER = struct.Struct("!4sBIQIBBH")
HEADER_SIZE = HEADER.size
MAX_DATAGRAM_SIZE = 1200
MAX_PAYLOAD_SIZE = MAX_DATAGRAM_SIZE - HEADER_SIZE
SAMPLE_WIDTH_BYTES = 4
UINT32_MASK = 0xFFFF_FFFF

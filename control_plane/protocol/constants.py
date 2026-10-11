"""Shared wire constants for Control Protocol v1."""

from struct import Struct

MAGIC = b"AC"
VERSION = 1
HEADER = Struct("!2sBBH")
HEADER_SIZE = HEADER.size
MAX_DATAGRAM_SIZE = 1200
MAX_TEXT_BYTES = 255

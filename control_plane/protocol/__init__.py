"""Control Protocol v1 wire API, independent from Audio Protocol v3."""

from .enums import Capability, ControlMessageType, NodeStatus, StreamState
from .packet import (
    ControlPacket,
    ErrorMessage,
    Goodbye,
    Heartbeat,
    Hello,
    StartStream,
    StopStream,
    StreamStatus,
)
from .parser import ControlPacketError, parse_packet
from .serializer import serialize_packet

__all__ = [
    "Capability", "ControlMessageType", "NodeStatus", "StreamState",
    "ControlPacket", "ErrorMessage", "Goodbye", "Heartbeat", "Hello",
    "StartStream", "StopStream", "StreamStatus", "ControlPacketError",
    "parse_packet", "serialize_packet",
]

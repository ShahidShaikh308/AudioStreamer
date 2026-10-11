"""Control Protocol v1 parser with append-only trailing-field compatibility."""

from __future__ import annotations

from .constants import HEADER, MAGIC, MAX_DATAGRAM_SIZE, VERSION
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


class ControlPacketError(ValueError):
    """Malformed or unsupported Control Protocol datagram."""


def _require(payload: memoryview, size: int, what: str) -> None:
    if len(payload) < size:
        raise ControlPacketError(f"truncated {what}")


def _text(payload: memoryview, offset: int, what: str) -> tuple[str, int]:
    _require(payload[offset:], 1, f"{what} length")
    length = payload[offset]
    offset += 1
    _require(payload[offset:], length, what)
    try:
        value = bytes(payload[offset:offset + length]).decode("utf-8", errors="strict")
    except UnicodeDecodeError as error:
        raise ControlPacketError(f"invalid UTF-8 in {what}") from error
    if b"\0" in bytes(payload[offset:offset + length]):
        raise ControlPacketError(f"NUL in {what}")
    return value, offset + length


def _optional_transaction(payload: memoryview, offset: int) -> int | None:
    trailing = len(payload) - offset
    if trailing == 1:
        raise ControlPacketError("truncated transaction ID")
    return int.from_bytes(payload[offset:offset + 2], "big") if trailing >= 2 else None


def parse_packet(data: bytes | bytearray | memoryview) -> ControlPacket:
    if not isinstance(data, (bytes, bytearray, memoryview)):
        raise TypeError("control datagram must be bytes-like")
    if len(data) > MAX_DATAGRAM_SIZE:
        raise ControlPacketError("control datagram exceeds the maximum size")
    if len(data) < HEADER.size:
        raise ControlPacketError("control datagram is shorter than its header")
    magic, version, raw_type, payload_length = HEADER.unpack_from(data)
    if magic != MAGIC:
        raise ControlPacketError("invalid control protocol magic")
    if version != VERSION:
        raise ControlPacketError(f"unsupported control protocol version: {version}")
    if payload_length != len(data) - HEADER.size:
        raise ControlPacketError("control payload length does not match datagram")
    try:
        message_type = ControlMessageType(raw_type)
    except ValueError as error:
        raise ControlPacketError(f"unsupported control message type: {raw_type}") from error
    payload = memoryview(data)[HEADER.size:]

    if message_type is ControlMessageType.HELLO:
        _require(payload, 12, "HELLO")
        node_id = int.from_bytes(payload[:8], "big")
        name, offset = _text(payload, 8, "node name")
        platform, offset = _text(payload, offset, "platform")
        _require(payload[offset:], 4, "HELLO capabilities and receive port")
        capabilities = int.from_bytes(payload[offset:offset + 2], "big")
        receive_port = int.from_bytes(payload[offset + 2:offset + 4], "big")
        if node_id == 0 or not name or not platform or capabilities & ~0x000F or receive_port == 0:
            raise ControlPacketError("invalid HELLO fields")
        message = Hello(node_id, name, platform, Capability(capabilities), receive_port)
    elif message_type is ControlMessageType.GOODBYE:
        _require(payload, 8, "GOODBYE")
        node_id = int.from_bytes(payload[:8], "big")
        if node_id == 0:
            raise ControlPacketError("node ID zero is reserved")
        message = Goodbye(node_id)
    elif message_type is ControlMessageType.HEARTBEAT:
        _require(payload, 9, "HEARTBEAT")
        node_id = int.from_bytes(payload[:8], "big")
        try:
            status = NodeStatus(payload[8])
        except ValueError as error:
            raise ControlPacketError("invalid heartbeat status") from error
        if node_id == 0 or status is NodeStatus.OFFLINE:
            raise ControlPacketError("invalid HEARTBEAT fields")
        message = Heartbeat(node_id, status)
    elif message_type in (ControlMessageType.START_STREAM, ControlMessageType.STOP_STREAM):
        _require(payload, 16, "stream command")
        sender = int.from_bytes(payload[:8], "big")
        receiver = int.from_bytes(payload[8:16], "big")
        tx = _optional_transaction(payload, 16)
        if sender == 0 or receiver == 0 or sender == receiver:
            raise ControlPacketError("invalid stream command node IDs")
        cls = StartStream if message_type is ControlMessageType.START_STREAM else StopStream
        message = cls(sender, receiver, tx)
    elif message_type is ControlMessageType.STREAM_STATUS:
        _require(payload, 19, "STREAM_STATUS")
        sender = int.from_bytes(payload[:8], "big")
        receiver = int.from_bytes(payload[8:16], "big")
        try:
            state = StreamState(payload[16])
        except ValueError as error:
            raise ControlPacketError("invalid stream state") from error
        code = int.from_bytes(payload[17:19], "big")
        message = StreamStatus(sender, receiver, state, code, _optional_transaction(payload, 19))
    else:
        _require(payload, 11, "ERROR")
        node_id = int.from_bytes(payload[:8], "big")
        code = int.from_bytes(payload[8:10], "big")
        text, offset = _text(payload, 10, "error message")
        tx = _optional_transaction(payload, offset)
        if node_id == 0:
            raise ControlPacketError("node ID zero is reserved")
        message = ErrorMessage(node_id, code, text, tx)
    return ControlPacket(message)

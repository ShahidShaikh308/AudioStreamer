"""Control Protocol v1 serializer using network byte order."""

from __future__ import annotations

from .constants import HEADER, MAGIC, MAX_DATAGRAM_SIZE, MAX_TEXT_BYTES, VERSION
from .enums import NodeStatus, StreamState
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


def _node_id(value: int) -> int:
    if type(value) is not int or not 0 < value <= 0xFFFF_FFFF_FFFF_FFFF:
        raise ValueError("node ID must be a nonzero unsigned 64-bit integer")
    return value


def _text(value: str, field: str, *, allow_empty: bool = False) -> bytes:
    if not isinstance(value, str):
        raise ValueError(f"{field} must be text")
    encoded = value.encode("utf-8")
    if (not encoded and not allow_empty) or len(encoded) > MAX_TEXT_BYTES:
        raise ValueError(f"{field} must contain 1..{MAX_TEXT_BYTES} UTF-8 bytes")
    if b"\0" in encoded:
        raise ValueError(f"{field} cannot contain NUL")
    return encoded


def _transaction_id(value: int | None) -> bytes:
    if value is None:
        return b""
    if type(value) is not int or not 0 <= value <= 0xFFFF:
        raise ValueError("transaction ID must be an unsigned 16-bit integer")
    return value.to_bytes(2, "big")


def serialize_packet(packet: ControlPacket) -> bytes:
    message = packet.message
    if isinstance(message, Hello):
        node_id = _node_id(message.node_id)
        name = _text(message.name, "name")
        platform = _text(message.platform, "platform")
        capabilities = int(message.capabilities)
        if not 0 <= capabilities <= 0x000F:
            raise ValueError("capabilities contain unsupported bits")
        if type(message.receive_port) is not int or not 1 <= message.receive_port <= 65535:
            raise ValueError("receive port must be between 1 and 65535")
        payload = (
            node_id.to_bytes(8, "big")
            + bytes((len(name),)) + name
            + bytes((len(platform),)) + platform
            + capabilities.to_bytes(2, "big")
            + message.receive_port.to_bytes(2, "big")
        )
    elif isinstance(message, Goodbye):
        payload = _node_id(message.node_id).to_bytes(8, "big")
    elif isinstance(message, Heartbeat):
        node_id = _node_id(message.node_id)
        status = NodeStatus(message.status)
        if status is NodeStatus.OFFLINE:
            raise ValueError("OFFLINE is not a valid heartbeat status")
        payload = node_id.to_bytes(8, "big") + bytes((int(status),))
    elif isinstance(message, (StartStream, StopStream)):
        sender = _node_id(message.sender_node_id)
        receiver = _node_id(message.receiver_node_id)
        if sender == receiver:
            raise ValueError("sender and receiver node IDs must differ")
        payload = sender.to_bytes(8, "big") + receiver.to_bytes(8, "big") + _transaction_id(message.transaction_id)
    elif isinstance(message, StreamStatus):
        sender = _node_id(message.sender_node_id)
        receiver = _node_id(message.receiver_node_id)
        if not 0 <= message.error_code <= 0xFFFF:
            raise ValueError("error code must be an unsigned 16-bit integer")
        state = StreamState(message.state)
        payload = (sender.to_bytes(8, "big") + receiver.to_bytes(8, "big")
                   + bytes((int(state),)) + message.error_code.to_bytes(2, "big")
                   + _transaction_id(message.transaction_id))
    elif isinstance(message, ErrorMessage):
        node_id = _node_id(message.node_id)
        text = _text(message.message, "error message", allow_empty=True)
        if not 0 <= message.error_code <= 0xFFFF:
            raise ValueError("error code must be an unsigned 16-bit integer")
        payload = (node_id.to_bytes(8, "big") + message.error_code.to_bytes(2, "big")
                   + bytes((len(text),)) + text + _transaction_id(message.transaction_id))
    else:
        raise TypeError(f"Unsupported control message: {type(message).__name__}")

    total_size = HEADER.size + len(payload)
    if total_size > MAX_DATAGRAM_SIZE:
        raise ValueError("control datagram exceeds the 1200-byte maximum")
    return HEADER.pack(MAGIC, VERSION, int(packet.message_type), len(payload)) + payload

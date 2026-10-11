"""Typed Control Protocol messages. Node IDs are unsigned 64-bit integers."""

from __future__ import annotations

from dataclasses import dataclass

from .enums import Capability, ControlMessageType, NodeStatus, StreamState


@dataclass(frozen=True, slots=True)
class Hello:
    node_id: int
    name: str
    platform: str
    capabilities: Capability
    receive_port: int


@dataclass(frozen=True, slots=True)
class Goodbye:
    node_id: int


@dataclass(frozen=True, slots=True)
class Heartbeat:
    node_id: int
    status: NodeStatus = NodeStatus.ONLINE


@dataclass(frozen=True, slots=True)
class StartStream:
    sender_node_id: int
    receiver_node_id: int
    transaction_id: int | None = None


@dataclass(frozen=True, slots=True)
class StopStream:
    sender_node_id: int
    receiver_node_id: int
    transaction_id: int | None = None


@dataclass(frozen=True, slots=True)
class StreamStatus:
    sender_node_id: int
    receiver_node_id: int
    state: StreamState
    error_code: int = 0
    transaction_id: int | None = None


@dataclass(frozen=True, slots=True)
class ErrorMessage:
    node_id: int
    error_code: int
    message: str = ""
    transaction_id: int | None = None


ControlMessage = Hello | Goodbye | Heartbeat | StartStream | StopStream | StreamStatus | ErrorMessage


@dataclass(frozen=True, slots=True)
class ControlPacket:
    message: ControlMessage

    @property
    def message_type(self) -> ControlMessageType:
        types = (
            (Hello, ControlMessageType.HELLO),
            (Goodbye, ControlMessageType.GOODBYE),
            (Heartbeat, ControlMessageType.HEARTBEAT),
            (StartStream, ControlMessageType.START_STREAM),
            (StopStream, ControlMessageType.STOP_STREAM),
            (StreamStatus, ControlMessageType.STREAM_STATUS),
            (ErrorMessage, ControlMessageType.ERROR),
        )
        for message_class, message_type in types:
            if isinstance(self.message, message_class):
                return message_type
        raise TypeError(f"Unsupported control message: {type(self.message).__name__}")

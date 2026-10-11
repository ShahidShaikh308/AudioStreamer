"""Typed, immutable events emitted by node implementations."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum

from core.node.state import NodeSnapshot


class Lane(str, Enum):
    SENDER = "sender"
    RECEIVER = "receiver"


class EventKind(str, Enum):
    STATE_CHANGED = "state_changed"
    SENDER_STATS = "sender_stats"
    RECEIVER_STATS = "receiver_stats"
    QUEUE_STATS = "queue_stats"
    FORMAT_CHANGED = "format_changed"
    ERROR = "error"
    SHUTDOWN_COMPLETE = "shutdown_complete"
    CONTROL_TOPOLOGY = "control_topology"


@dataclass(frozen=True, slots=True)
class ReceiverMetrics:
    packets_received: int
    bytes_received: int
    sample_rate: int
    channels: int
    last_packet_at: float


@dataclass(frozen=True, slots=True)
class NodeEvent:
    """A small status or diagnostic update safe to deliver across threads."""

    kind: EventKind
    snapshot: NodeSnapshot
    lane: Lane | None = None
    data: object | None = None
    message: str | None = None

"""Node capabilities and independent sender/receiver lifecycle state."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class LaneState(str, Enum):
    STOPPED = "stopped"
    STARTING = "starting"
    RUNNING = "running"
    STOPPING = "stopping"
    ERROR = "error"


class NodeStatus(str, Enum):
    STOPPED = "stopped"
    STARTING = "starting"
    RUNNING = "running"
    STOPPING = "stopping"
    ERROR = "error"


@dataclass(frozen=True, slots=True)
class CapabilitySet:
    can_capture: bool = False
    can_send: bool = False
    can_receive: bool = False
    can_play: bool = False


@dataclass(frozen=True, slots=True)
class NodeSnapshot:
    status: NodeStatus
    sender: LaneState
    receiver: LaneState


def aggregate_status(sender: LaneState, receiver: LaneState) -> NodeStatus:
    states = (sender, receiver)
    if LaneState.ERROR in states:
        return NodeStatus.ERROR
    if LaneState.RUNNING in states:
        return NodeStatus.RUNNING
    if LaneState.STARTING in states:
        return NodeStatus.STARTING
    if LaneState.STOPPING in states:
        return NodeStatus.STOPPING
    return NodeStatus.STOPPED

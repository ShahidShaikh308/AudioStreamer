"""Allowed per-capability node lifecycle transitions."""

from core.node.state import LaneState


_TRANSITIONS = {
    LaneState.STOPPED: {LaneState.STARTING},
    LaneState.STARTING: {
        LaneState.RUNNING, LaneState.STOPPING, LaneState.ERROR, LaneState.STOPPED,
    },
    LaneState.RUNNING: {LaneState.STOPPING, LaneState.ERROR, LaneState.STOPPED},
    LaneState.STOPPING: {LaneState.STOPPED, LaneState.ERROR},
    LaneState.ERROR: {LaneState.STOPPED, LaneState.STARTING},
}


def validate_transition(current: LaneState, target: LaneState) -> None:
    """Raise when a lane attempts a lifecycle transition outside the contract."""
    if target is current:
        return
    if target not in _TRANSITIONS[current]:
        raise RuntimeError(f"Invalid node lifecycle transition: {current.value} -> {target.value}")

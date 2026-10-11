"""Wire enumerations for Control Protocol v1."""

from enum import IntEnum, IntFlag


class ControlMessageType(IntEnum):
    HELLO = 1
    GOODBYE = 2
    HEARTBEAT = 3
    START_STREAM = 4
    STOP_STREAM = 5
    STREAM_STATUS = 6
    ERROR = 7


class Capability(IntFlag):
    CAPTURE = 1
    PLAYBACK = 2
    SENDER = 4
    RECEIVER = 8


class NodeStatus(IntEnum):
    OFFLINE = 0
    ONLINE = 1
    BUSY = 2
    STREAMING = 3
    ERROR = 4


class StreamState(IntEnum):
    RUNNING = 1
    STOPPED = 2
    FAILED = 3

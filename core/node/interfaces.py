"""Platform-neutral node API used by user interfaces and future control code."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable

from core.node.events import NodeEvent
from core.node.state import CapabilitySet, NodeSnapshot


@dataclass(frozen=True, slots=True)
class NodeSettings:
    device_name: str
    destination_ip: str
    destination_port: int
    receive_port: int


@dataclass(frozen=True, slots=True)
class NodeIdentity:
    device_name: str
    local_address: str
    protocol_version: int


NodeListener = Callable[[NodeEvent], None]


class INode(ABC):
    """Public lifecycle and status surface for any platform node."""

    @property
    @abstractmethod
    def capabilities(self) -> CapabilitySet: ...

    @property
    @abstractmethod
    def snapshot(self) -> NodeSnapshot: ...

    @property
    @abstractmethod
    def settings(self) -> NodeSettings: ...

    @property
    @abstractmethod
    def identity(self) -> NodeIdentity: ...

    @abstractmethod
    def subscribe(self, listener: NodeListener) -> Callable[[], None]: ...

    @abstractmethod
    def update_settings(self, settings: NodeSettings) -> None: ...

    @abstractmethod
    def save_configuration(self) -> None: ...

    @abstractmethod
    def start_sender(self) -> None: ...

    @abstractmethod
    def stop_sender(self) -> None: ...

    @abstractmethod
    def start_receiver(self) -> None: ...

    @abstractmethod
    def stop_receiver(self) -> None: ...

    @abstractmethod
    def shutdown(self) -> None:
        """Request graceful shutdown of every active capability."""

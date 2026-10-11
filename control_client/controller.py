"""UI-independent Device Manager state and Control Plane commands."""

from __future__ import annotations

import secrets
import socket
from collections.abc import Callable

from control_plane.manager import ControlEvent, ControlManager
from control_plane.protocol import Capability, NodeStatus
from control_plane.registry import NodeRecord

from .settings import DeviceManagerSettings


class DeviceManagerController:
    def __init__(self, settings: DeviceManagerSettings,
                 on_event: Callable[[ControlEvent], None]) -> None:
        self.settings = settings
        self.node_id = secrets.randbits(64) or 1
        self.manager = ControlManager(
            node_id=self.node_id,
            node_name=settings.node_name,
            platform="Windows Device Manager",
            capabilities=Capability(0),
            receive_port=settings.receive_port,
            status_provider=lambda: NodeStatus.ONLINE,
            command_handler=lambda _command: None,
            port=settings.control_port,
        )
        self._on_event = on_event
        self._unsubscribe: Callable[[], None] | None = None

    @property
    def peers(self) -> tuple[NodeRecord, ...]:
        return self.manager.registry.snapshot()

    @property
    def local_ip(self) -> str:
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            probe.connect(("255.255.255.255", self.settings.control_port))
            return probe.getsockname()[0]
        except OSError:
            return "127.0.0.1"
        finally:
            probe.close()

    def start(self) -> None:
        self._unsubscribe = self.manager.subscribe(self._on_event)
        self.manager.start()

    def update_settings(self, settings: DeviceManagerSettings) -> None:
        settings.validate()
        if settings.control_port != self.settings.control_port:
            raise ValueError("Changing the Control Port requires restarting the Device Manager")
        self.settings = settings
        self.manager.update_local(node_name=settings.node_name, capabilities=Capability(0),
                                 receive_port=settings.receive_port)

    def start_stream(self, sender_id: int, receiver_id: int) -> None:
        self.manager.send_start_stream(sender_id, receiver_id)

    def stop_stream(self, sender_id: int, receiver_id: int) -> None:
        self.manager.send_stop_stream(sender_id, receiver_id)

    def close(self) -> None:
        self.manager.stop()
        if self._unsubscribe is not None:
            self._unsubscribe()
            self._unsubscribe = None

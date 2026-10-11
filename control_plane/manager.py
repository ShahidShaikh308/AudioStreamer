"""Control Protocol policy: peer discovery, heartbeat and command dispatch."""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass
from enum import Enum
from typing import Callable

from control_plane.protocol import (
    Capability, ControlPacket, ErrorMessage, Goodbye, Heartbeat, Hello,
    NodeStatus, StartStream, StopStream, StreamStatus, StreamState,
    parse_packet, serialize_packet,
)
from control_plane.registry import NodeRecord, NodeRegistry
from control_plane.transport import ControlTransportClient

logger = logging.getLogger(__name__)
CONTROL_PORT = 55050
HEARTBEAT_SECONDS = 3.0
NODE_TIMEOUT_SECONDS = 10.0


class ControlEventKind(str, Enum):
    NODE_DISCOVERED = "node_discovered"
    NODE_UPDATED = "node_updated"
    NODE_LOST = "node_lost"
    COMMAND = "command"
    STREAM_STATUS = "stream_status"
    ERROR = "error"
    TRAFFIC_SENT = "traffic_sent"


@dataclass(frozen=True, slots=True)
class ControlEvent:
    kind: ControlEventKind
    node: NodeRecord | None = None
    message: object | None = None
    source: tuple[str, int] | None = None


ControlListener = Callable[[ControlEvent], None]
CommandHandler = Callable[[StartStream | StopStream], None]


class ControlManager:
    """Owns Control Protocol policy; all Windows UDP belongs to the broker."""

    CONTROL_PORT = CONTROL_PORT

    def __init__(
        self,
        *,
        node_id: int,
        node_name: str,
        platform: str,
        capabilities: Capability,
        receive_port: int,
        status_provider: Callable[[], NodeStatus],
        command_handler: CommandHandler,
        port: int = CONTROL_PORT,
    ) -> None:
        if not 0 < node_id <= 0xFFFF_FFFF_FFFF_FFFF:
            raise ValueError("node_id must be a nonzero unsigned 64-bit integer")
        if not 1 <= receive_port <= 65535 or not 49152 <= port <= 65535:
            raise ValueError("receive/control ports are invalid")
        self.node_id = node_id
        self.node_name = node_name
        self.platform = platform
        self.capabilities = Capability(capabilities)
        self.receive_port = receive_port
        self._status_provider = status_provider
        self._command_handler = command_handler
        self._port = port
        self.registry = NodeRegistry()
        self._listeners: set[ControlListener] = set()
        self._listeners_lock = threading.RLock()
        self._stats_lock = threading.Lock()
        self._packets_sent = 0
        self._bytes_sent = 0
        self._packets_received = 0
        self._bytes_received = 0
        self._closed = threading.Event()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._transport = ControlTransportClient(
            port, self._on_datagram, self._on_transport_error, self._on_transport_reconnected
        )

    def subscribe(self, listener: ControlListener) -> Callable[[], None]:
        with self._listeners_lock:
            self._listeners.add(listener)

        def unsubscribe() -> None:
            with self._listeners_lock:
                self._listeners.discard(listener)
        return unsubscribe

    @property
    def statistics(self) -> tuple[int, int, int, int]:
        with self._stats_lock:
            return self._packets_sent, self._bytes_sent, self._packets_received, self._bytes_received

    def start(self) -> None:
        if self._thread is not None:
            return
        self._transport.start()
        self._closed.clear()
        self._stop.clear()
        self._send_hello()
        self._thread = threading.Thread(target=self._heartbeat_loop,
                                        name=f"ControlManager-{self.node_id:016x}", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        if self._closed.is_set():
            return
        self._closed.set()
        self._stop.set()
        try:
            self._send(Goodbye(self.node_id), ("255.255.255.255", self._port))
        except OSError:
            logger.debug("control_goodbye_send_failed", exc_info=True)
        self._transport.close()
        thread, self._thread = self._thread, None
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=2.0)

    close = stop

    def update_local(self, *, node_name: str, capabilities: Capability, receive_port: int) -> None:
        if not node_name or not 1 <= receive_port <= 65535:
            raise ValueError("local node name and receive port are required")
        self.node_name = node_name
        self.capabilities = Capability(capabilities)
        self.receive_port = receive_port
        if not self._closed.is_set():
            self._send_hello()

    def send_start_stream(self, sender_id: int, receiver_id: int,
                          transaction_id: int | None = None) -> None:
        self._send_to_pair(StartStream(sender_id, receiver_id, transaction_id), sender_id, receiver_id)

    def send_stop_stream(self, sender_id: int, receiver_id: int,
                         transaction_id: int | None = None) -> None:
        self._send_to_pair(StopStream(sender_id, receiver_id, transaction_id), sender_id, receiver_id)

    def report_stream_status(
        self, sender_id: int, receiver_id: int, state: StreamState,
        *, error_code: int = 0, transaction_id: int | None = None,
    ) -> None:
        self._send_to_pair(StreamStatus(sender_id, receiver_id, state, error_code, transaction_id),
                           sender_id, receiver_id, skip_local=True)

    def report_error(
        self, node_id: int, error_code: int, message: str, transaction_id: int | None = None,
        sender_id: int | None = None, receiver_id: int | None = None,
    ) -> None:
        packet = ErrorMessage(node_id, error_code, message, transaction_id)
        if sender_id is None or receiver_id is None:
            self._send(packet, ("255.255.255.255", self._port))
        else:
            self._send_to_pair(packet, sender_id, receiver_id, skip_local=True)

    def _send_to_pair(self, message: object, sender_id: int, receiver_id: int,
                      *, skip_local: bool = False) -> None:
        packet = ControlPacket(message)  # serializer validates supported message type
        local_is_target = self.node_id in (sender_id, receiver_id)
        if local_is_target and not skip_local and isinstance(message, (StartStream, StopStream)):
            try:
                self._command_handler(message)
            except Exception as error:
                logger.exception("control_local_command_failed")
                self._emit(ControlEvent(ControlEventKind.ERROR, message=error))
                raise
        for node_id in {sender_id, receiver_id}:
            if node_id == self.node_id:
                continue
            self._send_to_node(packet, node_id)

    def _send_to_node(self, packet: ControlPacket, node_id: int) -> None:
        node = self.registry.get(node_id)
        if node is None:
            raise KeyError(f"Control Plane node {node_id:016x} is not online")
        # The HELLO receive_port is the Audio Plane port. Control messages use
        # the shared Control Plane port advertised by local configuration.
        self._send(packet.message, (node.address, self._port))

    def _send_hello(self, destination: tuple[str, int] = ("255.255.255.255", CONTROL_PORT)) -> None:
        # Use the configured port, including when responding to a HELLO source endpoint.
        if destination[0] == "255.255.255.255":
            destination = (destination[0], self._port)
        self._send(Hello(self.node_id, self.node_name, self.platform,
                         self.capabilities, self.receive_port), destination)

    def _send(self, message: object, destination: tuple[str, int]) -> None:
        if self._closed.is_set() and not isinstance(message, Goodbye):
            return
        data = serialize_packet(ControlPacket(message))
        self._transport.send(data, destination)
        with self._stats_lock:
            self._packets_sent += 1
            self._bytes_sent += len(data)
        self._emit(ControlEvent(ControlEventKind.TRAFFIC_SENT, message=message, source=destination))

    def _heartbeat_loop(self) -> None:
        while not self._stop.wait(HEARTBEAT_SECONDS):
            for node in self.registry.expire(NODE_TIMEOUT_SECONDS):
                self._emit(ControlEvent(ControlEventKind.NODE_LOST, node=node))
            status = self._status_provider()
            try:
                self._send_hello()
                self._send(Heartbeat(self.node_id, status), ("255.255.255.255", self._port))
            except (OSError, ValueError):
                logger.warning("control_heartbeat_send_failed", exc_info=True)

    def _on_datagram(self, data: bytes, address: tuple[str, int]) -> None:
        with self._stats_lock:
            self._packets_received += 1
            self._bytes_received += len(data)
        try:
            packet = parse_packet(data)
        except (ValueError, TypeError) as error:
            logger.debug("control_invalid_datagram source=%s error=%s", address, error)
            return
        message = packet.message
        if isinstance(message, (Hello, Heartbeat, Goodbye)) and message.node_id == self.node_id:
            return
        now = time.monotonic()
        if isinstance(message, Hello):
            node, added = self.registry.upsert(NodeRecord(
                message.node_id, message.name, message.platform, message.capabilities,
                address[0], message.receive_port, NodeStatus.ONLINE, now,
            ))
            self._emit(ControlEvent(ControlEventKind.NODE_DISCOVERED if added else ControlEventKind.NODE_UPDATED,
                                    node=node, message=message, source=address))
            if added:
                try:
                    self._send_hello(address)
                except OSError:
                    logger.warning("control_hello_response_failed", exc_info=True)
        elif isinstance(message, Heartbeat):
            node = self.registry.heartbeat(message.node_id, address[0], message.status, now)
            if node is not None:
                self._emit(ControlEvent(ControlEventKind.NODE_UPDATED, node=node,
                                        message=message, source=address))
        elif isinstance(message, Goodbye):
            node = self.registry.remove(message.node_id)
            if node is not None:
                self._emit(ControlEvent(ControlEventKind.NODE_LOST, node=node,
                                        message=message, source=address))
        elif isinstance(message, (StartStream, StopStream)):
            if self.node_id in (message.sender_node_id, message.receiver_node_id):
                try:
                    self._command_handler(message)
                except Exception as error:
                    self._emit(ControlEvent(ControlEventKind.ERROR, message=error, source=address))
                    self.report_error(self.node_id, 1, str(error), message.transaction_id,
                                      message.sender_node_id, message.receiver_node_id)
                self._emit(ControlEvent(ControlEventKind.COMMAND, message=message, source=address))
        elif isinstance(message, (StreamStatus, ErrorMessage)):
            self._emit(ControlEvent(ControlEventKind.STREAM_STATUS if isinstance(message, StreamStatus)
                                    else ControlEventKind.ERROR,
                                    message=message, source=address))

    def _on_transport_error(self, error: OSError) -> None:
        logger.warning("control_transport_error error=%s", error)
        self._emit(ControlEvent(ControlEventKind.ERROR, message=error))

    def _on_transport_reconnected(self) -> None:
        if self._closed.is_set():
            return
        try:
            self._send_hello()
        except OSError:
            logger.warning("control_reconnect_hello_failed", exc_info=True)

    def _emit(self, event: ControlEvent) -> None:
        with self._listeners_lock:
            listeners = tuple(self._listeners)
        for listener in listeners:
            try:
                listener(event)
            except Exception:
                logger.exception("control_listener_failed")

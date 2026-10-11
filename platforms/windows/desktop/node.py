"""Windows implementation of the platform-neutral node contract."""

from __future__ import annotations

import logging
import queue
import socket
import threading
import time
from collections.abc import Callable
from dataclasses import replace

from audio_plane.protocol import PROTOCOL_VERSION
from audio_plane.transport.udp_receiver import ReceiverStats
from control_plane.manager import ControlEvent, ControlEventKind, ControlManager
from control_plane.protocol import (
    Capability,
    NodeStatus,
    StartStream,
    StopStream,
    StreamState,
)
from control_plane.registry import NodeRecord
from core.audio.interfaces import (
    IAudioCapture,
    IAudioPlayback,
    IAudioQueue,
    IAudioReceiver,
    IAudioSender,
)
from core.node.events import EventKind, Lane, NodeEvent, ReceiverMetrics
from core.node.interfaces import INode, NodeIdentity, NodeListener, NodeSettings
from core.node.lifecycle import validate_transition
from core.node.state import CapabilitySet, LaneState, NodeSnapshot, aggregate_status
from platforms.windows.desktop.config import WindowsNodeConfig

logger = logging.getLogger(__name__)


CaptureFactory = Callable[[WindowsNodeConfig], IAudioCapture]
SenderFactory = Callable[[WindowsNodeConfig], IAudioSender]
QueueFactory = Callable[[], IAudioQueue]
ReceiverFactory = Callable[[int], IAudioReceiver]
PlayerFactory = Callable[[IAudioQueue], IAudioPlayback]


class WindowsAudioNode(INode):
    """Own Windows engines and independently manage capture/send and receive/play."""

    def __init__(
        self,
        config: WindowsNodeConfig,
        *,
        capture_factory: CaptureFactory,
        sender_factory: SenderFactory,
        receiver_factory: ReceiverFactory,
        queue_factory: QueueFactory,
        player_factory: PlayerFactory,
    ) -> None:
        self._config = config
        self._capture_factory = capture_factory
        self._sender_factory = sender_factory
        self._receiver_factory = receiver_factory
        self._queue_factory = queue_factory
        self._player_factory = player_factory
        self._lock = threading.RLock()
        self._listeners: set[NodeListener] = set()
        self._sender_state = LaneState.STOPPED
        self._receiver_state = LaneState.STOPPED
        self._sender_stop: threading.Event | None = None
        self._sender_thread: threading.Thread | None = None
        self._receiver_stop: threading.Event | None = None
        self._receiver_thread: threading.Thread | None = None
        self._playback_thread: threading.Thread | None = None
        self._last_queue_drops = 0
        self._capture: IAudioCapture | None = None
        self._sender: IAudioSender | None = None
        self._receiver: IAudioReceiver | None = None
        self._player: IAudioPlayback | None = None
        self._shutdown_requested = False
        self._shutdown_complete_emitted = False
        self._control_manager: ControlManager | None = None
        self._unsubscribe_control: Callable[[], None] | None = None
        self._active_stream_pair: tuple[int, int] | None = None
        self._active_transaction_id: int | None = None

    @property
    def capabilities(self) -> CapabilitySet:
        return CapabilitySet(
            can_capture=True, can_send=True, can_receive=True, can_play=True
        )

    @property
    def snapshot(self) -> NodeSnapshot:
        with self._lock:
            return NodeSnapshot(
                aggregate_status(self._sender_state, self._receiver_state),
                self._sender_state,
                self._receiver_state,
            )

    @property
    def settings(self) -> NodeSettings:
        with self._lock:
            return NodeSettings(
                self._config.device_name,
                self._config.receiver_ip,
                self._config.receiver_port,
                self._config.receive_port,
            )

    @property
    def identity(self) -> NodeIdentity:
        with self._lock:
            config = self._config
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            probe.connect((config.receiver_ip, config.receiver_port))
            address = probe.getsockname()[0]
        except OSError:
            address = "127.0.0.1"
        finally:
            probe.close()
        return NodeIdentity(
            config.device_name,
            address,
            PROTOCOL_VERSION,
            config.node_id,
            config.control_port,
        )

    @property
    def known_nodes(self) -> tuple[NodeRecord, ...]:
        manager = self._control_manager
        return manager.registry.snapshot() if manager is not None else ()

    def attach_control_manager(self, manager: ControlManager) -> None:
        """Register this node with the independent Control Plane at composition time."""
        if self._control_manager is not None:
            raise RuntimeError("Control manager is already attached")
        self._control_manager = manager
        self._unsubscribe_control = manager.subscribe(self._control_event)
        manager.start()

    def start_stream(self, sender_node_id: int, receiver_node_id: int) -> None:
        if self._control_manager is None:
            raise RuntimeError("Control Plane is not running")
        self._control_manager.send_start_stream(sender_node_id, receiver_node_id)

    def stop_stream(self, sender_node_id: int, receiver_node_id: int) -> None:
        if self._control_manager is None:
            raise RuntimeError("Control Plane is not running")
        self._control_manager.send_stop_stream(sender_node_id, receiver_node_id)

    def _control_event(self, event: ControlEvent) -> None:
        if event.kind in {
            ControlEventKind.NODE_DISCOVERED,
            ControlEventKind.NODE_UPDATED,
            ControlEventKind.NODE_LOST,
        }:
            manager = self._control_manager
            self._emit(
                EventKind.CONTROL_TOPOLOGY,
                data=manager.registry.snapshot() if manager else (),
            )

    def handle_control_command(self, message: StartStream | StopStream) -> None:
        node_id = int(self._config.node_id, 16)
        if node_id not in (message.sender_node_id, message.receiver_node_id):
            logger.warning(
                "control_command_not_addressed_to_node node_id=%016x", node_id
            )
            return
        self._active_stream_pair = (message.sender_node_id, message.receiver_node_id)
        self._active_transaction_id = message.transaction_id
        starting = isinstance(message, StartStream)
        if message.sender_node_id == node_id:
            if starting:
                peer = self._control_manager.registry.get(message.receiver_node_id)
                if peer is None:
                    raise RuntimeError("Receiver is no longer discovered")
                if not peer.capabilities & Capability.RECEIVER:
                    raise RuntimeError(
                        "Selected node does not advertise receiver capability"
                    )
                settings = self.settings
                self.update_settings(
                    replace(
                        settings,
                        destination_ip=peer.address,
                        destination_port=peer.receive_port,
                    )
                )
                self.start_sender()
            else:
                self.stop_sender()
        if message.receiver_node_id == node_id:
            if starting:
                peer = self._control_manager.registry.get(message.sender_node_id)
                if peer is not None and not peer.capabilities & Capability.SENDER:
                    raise RuntimeError(
                        "Selected node does not advertise sender capability"
                    )
                self.start_receiver()
            else:
                self.stop_receiver()
        active_state = (
            self.snapshot.sender
            if message.sender_node_id == node_id
            else self.snapshot.receiver
        )
        if active_state is LaneState.RUNNING:
            self._control_manager.report_stream_status(
                message.sender_node_id,
                message.receiver_node_id,
                StreamState.RUNNING,
                transaction_id=message.transaction_id,
            )
        elif active_state is LaneState.STOPPED and not starting:
            self._control_manager.report_stream_status(
                message.sender_node_id,
                message.receiver_node_id,
                StreamState.STOPPED,
                transaction_id=message.transaction_id,
            )

    def subscribe(self, listener: NodeListener) -> Callable[[], None]:
        with self._lock:
            self._listeners.add(listener)

        def unsubscribe() -> None:
            with self._lock:
                self._listeners.discard(listener)

        return unsubscribe

    def update_settings(self, settings: NodeSettings) -> None:
        with self._lock:
            if self._shutdown_requested:
                raise RuntimeError("Node is shutting down")
            updated = replace(
                self._config,
                device_name=settings.device_name,
                receiver_ip=settings.destination_ip,
                receiver_port=settings.destination_port,
                receive_port=settings.receive_port,
            )
            updated.validate()
            self._config = updated

    def save_configuration(self) -> None:
        with self._lock:
            self._config.save()
        logger.info(
            "configuration_saved device=%s destination=%s:%d receive_port=%d",
            self._config.device_name,
            self._config.receiver_ip,
            self._config.receiver_port,
            self._config.receive_port,
        )

    def start_sender(self) -> None:
        with self._lock:
            if self._shutdown_requested:
                raise RuntimeError("Node is shutting down")
            if self._sender_thread is not None:
                return
            self._set_lane_locked(Lane.SENDER, LaneState.STARTING)
            stop_event = threading.Event()
            thread = threading.Thread(
                target=self._sender_worker,
                args=(stop_event,),
                name="AudioStreamer-Sender",
                daemon=False,
            )
            self._sender_stop, self._sender_thread = stop_event, thread
            thread.start()

    def stop_sender(self) -> None:
        with self._lock:
            if self._sender_stop is not None:
                self._set_lane_locked(Lane.SENDER, LaneState.STOPPING)
                self._sender_stop.set()
            elif self._sender_state is LaneState.ERROR:
                self._set_lane_locked(Lane.SENDER, LaneState.STOPPED)

    def start_receiver(self) -> None:
        with self._lock:
            if self._shutdown_requested:
                raise RuntimeError("Node is shutting down")
            if self._receiver_thread is not None or self._playback_thread is not None:
                return
            stop_event = threading.Event()
            packet_queue = self._queue_factory()
            receiver = self._receiver_factory(self._config.receive_port)
            player = self._player_factory(packet_queue)
            self._set_lane_locked(Lane.RECEIVER, LaneState.STARTING)
            playback = threading.Thread(
                target=self._playback_worker,
                args=(player, packet_queue, stop_event),
                name="AudioStreamer-Playback",
                daemon=False,
            )
            receive = threading.Thread(
                target=self._receiver_worker,
                args=(receiver, packet_queue, stop_event),
                name="AudioStreamer-Receiver",
                daemon=False,
            )
            self._receiver_stop = stop_event
            self._receiver, self._player = receiver, player
            self._playback_thread, self._receiver_thread = playback, receive
            self._last_queue_drops = 0
            self._emit(
                EventKind.STATE_CHANGED, Lane.RECEIVER, data=self._config.receive_port
            )
            playback.start()
            receive.start()

    def stop_receiver(self) -> None:
        with self._lock:
            if self._receiver_stop is not None:
                self._set_lane_locked(Lane.RECEIVER, LaneState.STOPPING)
                self._receiver_stop.set()
            elif self._receiver_state is LaneState.ERROR:
                self._set_lane_locked(Lane.RECEIVER, LaneState.STOPPED)

    def shutdown(self) -> None:
        with self._lock:
            self._shutdown_requested = True
        self.stop_sender()
        self.stop_receiver()
        if self._control_manager is not None:
            self._control_manager.stop()
            if self._unsubscribe_control is not None:
                self._unsubscribe_control()
                self._unsubscribe_control = None
        self._finish_shutdown_if_idle()

    def _sender_worker(self, stop_event: threading.Event) -> None:
        capture: IAudioCapture | None = None
        sender: IAudioSender | None = None
        try:
            config = self._config
            capture = self._capture_factory(config)
            self._capture = capture
            capture.start()
            if stop_event.is_set():
                return
            sender = self._sender_factory(config)
            self._sender = sender
            pcm_format = capture.format
            if pcm_format is None:
                raise RuntimeError("Capture started without a PCM format")
            logger.info(
                "sender_ready device=%s rate=%d channels=%d destination=%s:%d protocol=v%d",
                capture.device_name,
                pcm_format.sample_rate,
                pcm_format.channels,
                config.receiver_ip,
                config.receiver_port,
                PROTOCOL_VERSION,
            )
            self._set_lane(Lane.SENDER, LaneState.RUNNING)
            self._emit(
                EventKind.FORMAT_CHANGED,
                Lane.SENDER,
                data=(capture.device_name, pcm_format.sample_rate, pcm_format.channels),
            )
            last_report, last_send_error = time.monotonic(), 0.0
            while not stop_event.is_set():
                try:
                    chunk = capture.read(timeout=0.5)
                except queue.Empty:
                    continue
                try:
                    sender.send_chunk(chunk, pcm_format)
                except OSError as error:
                    now = time.monotonic()
                    if now - last_send_error >= 2.0:
                        logger.warning(
                            "sender_network_error destination=%s:%d error=%s",
                            config.receiver_ip,
                            config.receiver_port,
                            error,
                        )
                        self._emit(EventKind.ERROR, Lane.SENDER, message=str(error))
                        last_send_error = now
                    stop_event.wait(0.1)
                    continue
                now = time.monotonic()
                if now - last_report >= 1.0:
                    self._emit(
                        EventKind.SENDER_STATS,
                        Lane.SENDER,
                        data=(
                            sender.packets_sent,
                            sender.bytes_sent,
                            capture.dropped_chunks,
                            capture.callback_status_events,
                        ),
                    )
                    last_report = now
        except Exception as error:
            logger.exception("sender_worker_failed")
            self._emit(EventKind.ERROR, Lane.SENDER, message=str(error))
            self._set_lane(Lane.SENDER, LaneState.ERROR)
        finally:
            if sender is not None:
                try:
                    self._emit(
                        EventKind.SENDER_STATS,
                        Lane.SENDER,
                        data=(
                            sender.packets_sent,
                            sender.bytes_sent,
                            capture.dropped_chunks if capture else 0,
                            capture.callback_status_events if capture else 0,
                        ),
                    )
                    sender.close()
                except Exception:
                    logger.exception("sender_cleanup_failed")
            if capture is not None:
                try:
                    capture.close()
                except Exception:
                    logger.exception("capture_cleanup_failed")
            with self._lock:
                self._capture = self._sender = None
                self._sender_thread = self._sender_stop = None
                if self._sender_state is not LaneState.ERROR:
                    self._set_lane_locked(Lane.SENDER, LaneState.STOPPED)
            self._finish_shutdown_if_idle()

    def _receiver_worker(
        self,
        receiver: IAudioReceiver,
        packet_queue: IAudioQueue,
        stop_event: threading.Event,
    ) -> None:
        try:
            receiver.run(
                stop_event,
                on_packet=packet_queue.offer,
                on_stats=lambda stats: self._receiver_stats(stats, packet_queue),
                on_error=lambda error: self._emit(
                    EventKind.ERROR, Lane.RECEIVER, message=str(error)
                ),
                on_started=lambda: self._receiver_started(stop_event),
            )
        except Exception as error:
            logger.exception("receiver_worker_failed")
            self._emit(EventKind.ERROR, Lane.RECEIVER, message=str(error))
            self._set_lane(Lane.RECEIVER, LaneState.ERROR)
        finally:
            stop_event.set()
            with self._lock:
                self._receiver_thread = None
                self._receiver = None
                if self._receiver_state is not LaneState.ERROR:
                    self._set_lane_locked(Lane.RECEIVER, LaneState.STOPPED)
            self._finish_receiver_cleanup()

    def _playback_worker(
        self,
        player: IAudioPlayback,
        packet_queue: IAudioQueue,
        stop_event: threading.Event,
    ) -> None:
        try:
            player.run(
                stop_event,
                on_error=lambda error: self._emit(
                    EventKind.ERROR, Lane.RECEIVER, message=str(error)
                ),
            )
        except Exception as error:
            logger.exception("playback_worker_failed")
            self._emit(EventKind.ERROR, Lane.RECEIVER, message=str(error))
            self._set_lane(Lane.RECEIVER, LaneState.ERROR)
            stop_event.set()
        finally:
            if not stop_event.is_set():
                stop_event.set()
                self._set_lane(Lane.RECEIVER, LaneState.ERROR)
            logger.info(
                "playback_queue_final buffered_ms=%.2f dropped_packets=%d overflow_events=%d",
                packet_queue.buffered_ms,
                packet_queue.dropped_packets,
                packet_queue.overflow_events,
            )
            with self._lock:
                self._playback_thread = None
                self._player = None
            self._finish_receiver_cleanup()

    def _finish_receiver_cleanup(self) -> None:
        with self._lock:
            if self._receiver_thread is not None or self._playback_thread is not None:
                return
            self._receiver_stop = None
            if self._receiver_state is not LaneState.ERROR:
                self._set_lane_locked(Lane.RECEIVER, LaneState.STOPPED)
        self._finish_shutdown_if_idle()

    def _receiver_stats(self, stats: ReceiverStats, packet_queue: IAudioQueue) -> None:
        self._emit(
            EventKind.RECEIVER_STATS,
            Lane.RECEIVER,
            data=ReceiverMetrics(
                packets_received=stats.packets_received,
                bytes_received=stats.bytes_received,
                sample_rate=stats.sample_rate,
                channels=stats.channels,
                last_packet_at=stats.last_packet_at,
            ),
        )
        self._emit(
            EventKind.QUEUE_STATS,
            Lane.RECEIVER,
            data=(packet_queue.buffered_ms, packet_queue.dropped_packets),
        )
        if packet_queue.dropped_packets > self._last_queue_drops:
            logger.warning(
                "playback_queue_overflow dropped_total=%d buffered_ms=%.2f",
                packet_queue.dropped_packets,
                packet_queue.buffered_ms,
            )
            self._last_queue_drops = packet_queue.dropped_packets
        logger.info(
            "receiver_stats packets=%d bytes=%d sequence_drops=%d invalid=%d rate=%d channels=%d queued_drops=%d",
            stats.packets_received,
            stats.bytes_received,
            stats.dropped_packets,
            stats.invalid_packets,
            stats.sample_rate,
            stats.channels,
            packet_queue.dropped_packets,
        )

    def _receiver_started(self, stop_event: threading.Event) -> None:
        if not stop_event.is_set():
            self._set_lane(Lane.RECEIVER, LaneState.RUNNING)

    def _set_lane(self, lane: Lane, state: LaneState) -> None:
        with self._lock:
            self._set_lane_locked(lane, state)

    def _set_lane_locked(self, lane: Lane, state: LaneState) -> None:
        attribute = "_sender_state" if lane is Lane.SENDER else "_receiver_state"
        if getattr(self, attribute) is state:
            return
        validate_transition(getattr(self, attribute), state)
        setattr(self, attribute, state)
        self._emit(EventKind.STATE_CHANGED, lane)
        manager = self._control_manager
        if manager is not None:
            manager.update_local(
                node_name=self._config.device_name,
                capabilities=self._control_capabilities(),
                receive_port=self._config.receive_port,
            )
            pair = self._active_stream_pair
            if pair is not None and state in {
                LaneState.RUNNING,
                LaneState.STOPPED,
                LaneState.ERROR,
            }:
                stream_state = {
                    LaneState.RUNNING: StreamState.RUNNING,
                    LaneState.STOPPED: StreamState.STOPPED,
                    LaneState.ERROR: StreamState.FAILED,
                }[state]
                manager.report_stream_status(
                    *pair,
                    stream_state,
                    error_code=1 if state is LaneState.ERROR else 0,
                    transaction_id=self._active_transaction_id,
                )

    def _emit(
        self,
        kind: EventKind,
        lane: Lane | None = None,
        *,
        data=None,
        message: str | None = None,
    ) -> None:
        with self._lock:
            snapshot = NodeSnapshot(
                aggregate_status(self._sender_state, self._receiver_state),
                self._sender_state,
                self._receiver_state,
            )
            listeners = tuple(self._listeners)
        event = NodeEvent(kind, snapshot, lane, data, message)
        for listener in listeners:
            try:
                listener(event)
            except Exception:
                logger.exception("node_event_listener_failed")

    def _finish_shutdown_if_idle(self) -> None:
        with self._lock:
            idle = (
                self._sender_thread is None
                and self._receiver_thread is None
                and self._playback_thread is None
            )
            if (
                not self._shutdown_requested
                or not idle
                or self._shutdown_complete_emitted
            ):
                return
            self._shutdown_complete_emitted = True
        self._emit(EventKind.SHUTDOWN_COMPLETE)

    def _control_capabilities(self) -> Capability:
        capabilities = Capability.CAPTURE | Capability.PLAYBACK
        capabilities |= Capability.SENDER | Capability.RECEIVER
        return capabilities

    def control_status(self) -> NodeStatus:
        snapshot = self.snapshot
        if snapshot.status.value == "error":
            return NodeStatus.ERROR
        if snapshot.sender in {
            LaneState.STARTING,
            LaneState.STOPPING,
        } or snapshot.receiver in {LaneState.STARTING, LaneState.STOPPING}:
            return NodeStatus.BUSY
        if (
            snapshot.sender is LaneState.RUNNING
            or snapshot.receiver is LaneState.RUNNING
        ):
            return NodeStatus.STREAMING
        return NodeStatus.ONLINE

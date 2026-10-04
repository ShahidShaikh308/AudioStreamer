"""Receive and validate AudioStreamer packets without audio-device concerns."""

from __future__ import annotations

import logging
import socket
import threading
import time
from dataclasses import dataclass
from typing import Callable

from audio_plane.protocol import AudioPacket, MAX_DATAGRAM_SIZE, PacketError, parse_packet

logger = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class ReceiverStats:
    packets_received: int = 0
    bytes_received: int = 0
    dropped_packets: int = 0
    invalid_packets: int = 0
    sample_rate: int = 0
    channels: int = 0
    stream_id: int = 0
    last_packet_at: float = 0.0


class UdpAudioReceiver:
    """Bind a UDP port, validate v3 datagrams, and deliver packets by callback."""

    def __init__(self, port: int, *, bind_host: str = "0.0.0.0"):
        if not 1 <= port <= 65535:
            raise ValueError("port must be between 1 and 65535")
        self.port = port
        self.bind_host = bind_host
        self._packets_received = 0
        self._bytes_received = 0
        self._dropped_packets = 0
        self._invalid_packets = 0
        self._sample_rate = 0
        self._channels = 0
        self._stream_id = 0
        self._last_sequence: int | None = None
        self._last_packet_at = 0.0
        self._last_error_notice = 0.0
        self._last_gap_notice = 0.0
        self._last_stats_at = 0.0

    def run(
        self,
        stop_event: threading.Event,
        on_packet: Callable[[AudioPacket], None],
        on_stats: Callable[[ReceiverStats], None] | None = None,
        on_error: Callable[[BaseException], None] | None = None,
        on_started: Callable[[], None] | None = None,
    ) -> None:
        """Receive until asked to stop; socket timeouts make shutdown prompt."""
        logger.info("receiver_start port=%d bind=%s", self.port, self.bind_host)
        while not stop_event.is_set():
            udp: socket.socket | None = None
            try:
                udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                udp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                udp.bind((self.bind_host, self.port))
                udp.settimeout(0.25)
                logger.info("receiver_listening port=%d bind=%s", self.port, self.bind_host)
                if on_started is not None:
                    on_started()
                self._receive_loop(udp, stop_event, on_packet, on_stats)
            except OSError as error:
                if stop_event.is_set():
                    break
                now = time.monotonic()
                if now - self._last_error_notice >= 5.0:
                    logger.warning("receiver_socket_error port=%d error=%s", self.port, error)
                    if on_error is not None:
                        on_error(error)
                    self._last_error_notice = now
                stop_event.wait(0.5)
            finally:
                if udp is not None:
                    udp.close()
        self._emit_stats(on_stats, force=True)
        logger.info("receiver_stop port=%d", self.port)

    def _receive_loop(
        self,
        udp: socket.socket,
        stop_event: threading.Event,
        on_packet: Callable[[AudioPacket], None],
        on_stats: Callable[[ReceiverStats], None] | None,
    ) -> None:
        while not stop_event.is_set():
            try:
                datagram, address = udp.recvfrom(MAX_DATAGRAM_SIZE + 1)
            except socket.timeout:
                self._emit_stats(on_stats)
                continue
            except OSError:
                raise

            self._bytes_received += len(datagram)
            try:
                packet = parse_packet(datagram)
            except (PacketError, TypeError) as error:
                self._invalid_packets += 1
                if self._invalid_packets == 1 or self._invalid_packets & (self._invalid_packets - 1) == 0:
                    logger.warning(
                        "invalid_packet remote=%s bytes=%d count=%d reason=%s",
                        address[0], len(datagram), self._invalid_packets, error,
                    )
                self._emit_stats(on_stats)
                continue

            if not self._accept_sequence(packet):
                self._emit_stats(on_stats)
                continue

            self._packets_received += 1
            self._sample_rate = packet.sample_rate
            self._channels = packet.channels
            self._last_packet_at = time.monotonic()
            try:
                on_packet(packet)
            except Exception:
                logger.exception("receiver_packet_consumer_error sequence=%d", packet.sequence_number)
            self._emit_stats(on_stats)

    def _accept_sequence(self, packet: AudioPacket) -> bool:
        if packet.stream_id != self._stream_id:
            logger.info("receiver_stream_changed old=%d new=%d", self._stream_id, packet.stream_id)
            self._stream_id = packet.stream_id
            self._last_sequence = packet.sequence_number
            return True
        previous = self._last_sequence
        if previous is None:
            self._last_sequence = packet.sequence_number
            return True

        distance = (packet.sequence_number - previous) & 0xFFFF_FFFF
        if distance == 0 or distance >= 0x8000_0000:
            self._dropped_packets += 1
            return False
        if distance > 1:
            missing = distance - 1
            self._dropped_packets += missing
            now = time.monotonic()
            if now - self._last_gap_notice >= 1.0:
                logger.warning(
                    "sequence_gap stream_id=%d missing_total=%d previous=%d current=%d",
                    packet.stream_id, self._dropped_packets, previous, packet.sequence_number,
                )
                self._last_gap_notice = now
        self._last_sequence = packet.sequence_number
        return True

    def _emit_stats(
        self,
        on_stats: Callable[[ReceiverStats], None] | None,
        *,
        force: bool = False,
    ) -> None:
        if on_stats is None:
            return
        now = time.monotonic()
        if not force and now - self._last_stats_at < 1.0:
            return
        self._last_stats_at = now
        on_stats(
            ReceiverStats(
                packets_received=self._packets_received,
                bytes_received=self._bytes_received,
                dropped_packets=self._dropped_packets,
                invalid_packets=self._invalid_packets,
                sample_rate=self._sample_rate,
                channels=self._channels,
                stream_id=self._stream_id,
                last_packet_at=self._last_packet_at,
            )
        )

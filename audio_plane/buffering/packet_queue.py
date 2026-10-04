"""Thread-safe, duration-bounded queue for received PCM packets."""

from __future__ import annotations

import threading
import time
from collections import deque

from audio_plane.protocol import AudioPacket, SAMPLE_WIDTH_BYTES


class AudioPacketQueue:
    """Bound queued audio by playout duration and a hard packet-count ceiling.

    On overflow, the oldest packet is removed to preserve freshness and keep
    latency bounded. A new stream ID flushes stale packets from the prior run.
    """

    def __init__(self, *, target_buffer_ms: int = 80, max_packets: int = 256):
        if target_buffer_ms <= 0 or max_packets <= 0:
            raise ValueError("queue limits must be positive")
        self._target_duration = target_buffer_ms / 1000.0
        self._max_packets = max_packets
        self._packets: deque[tuple[AudioPacket, float]] = deque()
        self._duration = 0.0
        self._stream_id: int | None = None
        self._condition = threading.Condition()
        self._dropped_packets = 0
        self._overflow_events = 0

    @property
    def dropped_packets(self) -> int:
        with self._condition:
            return self._dropped_packets

    @property
    def overflow_events(self) -> int:
        with self._condition:
            return self._overflow_events

    @property
    def buffered_ms(self) -> float:
        with self._condition:
            return self._duration * 1000.0

    def offer(self, packet: AudioPacket) -> bool:
        """Enqueue a packet; return false only when that packet cannot fit."""
        duration = len(packet.payload) / (
            packet.sample_rate * packet.channels * SAMPLE_WIDTH_BYTES
        )
        with self._condition:
            if self._stream_id != packet.stream_id:
                self._packets.clear()
                self._duration = 0.0
                self._stream_id = packet.stream_id

            if duration > self._target_duration:
                self._dropped_packets += 1
                self._overflow_events += 1
                return False

            dropped = False
            while self._packets and (
                len(self._packets) >= self._max_packets
                or self._duration + duration > self._target_duration
            ):
                _, removed_duration = self._packets.popleft()
                self._duration -= removed_duration
                self._dropped_packets += 1
                dropped = True
            if dropped:
                self._overflow_events += 1

            self._packets.append((packet, duration))
            self._duration += duration
            self._condition.notify()
            return True

    def take(self, timeout: float = 0.25) -> AudioPacket | None:
        """Wait for a packet, returning None on timeout rather than spinning."""
        deadline = time.monotonic() + timeout
        with self._condition:
            while not self._packets:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._condition.wait(remaining)
            packet, duration = self._packets.popleft()
            self._duration = max(0.0, self._duration - duration)
            return packet

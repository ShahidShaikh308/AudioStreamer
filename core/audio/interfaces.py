"""Structural contracts connecting platform audio engines to the node."""

from __future__ import annotations

import threading
from collections.abc import Callable
from typing import Protocol

from audio_plane.models import PcmChunk, PcmFormat
from audio_plane.protocol import AudioPacket


class IAudioCapture(Protocol):
    format: PcmFormat | None
    device_name: str
    dropped_chunks: int
    callback_status_events: int

    def start(self) -> IAudioCapture: ...
    def read(self, timeout: float | None = None) -> PcmChunk: ...
    def close(self) -> None: ...


class IAudioSender(Protocol):
    packets_sent: int
    bytes_sent: int

    def send_chunk(self, chunk: PcmChunk, audio_format: PcmFormat) -> int: ...
    def close(self) -> None: ...


class IAudioQueue(Protocol):
    buffered_ms: float
    dropped_packets: int
    overflow_events: int

    def offer(self, packet: AudioPacket) -> bool: ...
    def take(self, timeout: float | None = None) -> AudioPacket | None: ...


class IAudioReceiver(Protocol):
    def run(
        self,
        stop_event: threading.Event,
        on_packet: Callable[[AudioPacket], None],
        on_stats: Callable[[object], None] | None = None,
        on_error: Callable[[BaseException], None] | None = None,
        on_started: Callable[[], None] | None = None,
    ) -> None: ...


class IAudioPlayback(Protocol):
    def run(
        self,
        stop_event: threading.Event,
        on_error: Callable[[BaseException], None] | None = None,
    ) -> None: ...

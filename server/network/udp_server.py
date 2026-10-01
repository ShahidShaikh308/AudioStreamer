"""Send captured WASAPI PCM chunks to a UDP receiver."""

from __future__ import annotations

import logging
import queue
import secrets
import socket
import time
from collections.abc import Callable
from threading import Event

from server.audio.capture import AudioChunk, AudioFormat, WasapiLoopbackCapture
from server.network.protocol import (
    UINT32_MASK,
    AudioPacket,
    ProtocolVersion,
    encode_packet,
    iter_pcm_payloads,
)

logger = logging.getLogger(__name__)


class UdpAudioServer:
    """Packetize float32 capture chunks and send them to one UDP destination."""

    def __init__(
        self,
        destination_host: str = "127.0.0.1",
        destination_port: int = 5005,
        protocol_version: ProtocolVersion | int = ProtocolVersion.CURRENT,
    ):
        if not 1 <= destination_port <= 65535:
            raise ValueError("destination_port must be between 1 and 65535")
        self.destination = (destination_host, destination_port)
        try:
            self.protocol_version = ProtocolVersion(protocol_version)
        except ValueError as exc:
            raise ValueError(
                f"unsupported protocol version: {protocol_version}"
            ) from exc
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.stream_id = secrets.randbits(64) or 1
        self._next_sequence = 0
        self.packets_sent = 0

    def send_chunk(self, chunk: AudioChunk, audio_format: AudioFormat) -> int:
        """Split and send one capture chunk; return the number of datagrams sent."""
        if audio_format.encoding != "float32" or audio_format.sample_width_bytes != 4:
            raise ValueError("UDP protocol currently supports float32 capture only")
        if audio_format.sample_rate < 1 or audio_format.channels < 1:
            raise ValueError(
                "capture format must have a positive rate and channel count"
            )
        expected = (
            chunk.frames * audio_format.channels * audio_format.sample_width_bytes
        )
        if len(chunk.data) != expected:
            raise ValueError(
                f"capture buffer has {len(chunk.data)} bytes; expected {expected}"
            )

        capture_timestamp_ns = int(chunk.captured_at * 1_000_000_000)
        frame_offset = 0
        frame_size = audio_format.channels * audio_format.sample_width_bytes
        count = 0
        for payload in iter_pcm_payloads(chunk.data, audio_format.channels):
            packet = AudioPacket(
                sequence_number=self._next_sequence,
                timestamp_ns=(
                    capture_timestamp_ns
                    + frame_offset * 1_000_000_000 // audio_format.sample_rate
                ),
                sample_rate=audio_format.sample_rate,
                channels=audio_format.channels,
                payload=payload,
                stream_id=self.stream_id,
                protocol_version=self.protocol_version,
            )
            self._socket.sendto(encode_packet(packet), self.destination)
            self._next_sequence = (self._next_sequence + 1) & UINT32_MASK
            self.packets_sent += 1
            count += 1
            frame_offset += len(payload) // frame_size
        return count

    def stream_capture(
        self,
        capture: WasapiLoopbackCapture,
        *,
        stop_event: Event | None = None,
        on_stats: Callable[[int, int], None] | None = None,
    ) -> None:
        """Forward captured chunks until its optional stop event is set."""
        if capture.format is None:
            raise RuntimeError("capture must be started before streaming")

        last_report = time.monotonic()
        while stop_event is None or not stop_event.is_set():
            try:
                chunk = capture.read(timeout=0.5)
            except queue.Empty:
                continue
            self.send_chunk(chunk, capture.format)
            now = time.monotonic()
            if now - last_report >= 0.25:
                if on_stats is not None:
                    on_stats(self.packets_sent, capture.dropped_chunks)
                logger.debug(
                    "Sent %d packets; capture queue drops=%d",
                    self.packets_sent,
                    capture.dropped_chunks,
                )
                last_report = now

    def close(self) -> None:
        self._socket.close()

    def __enter__(self) -> UdpAudioServer:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()

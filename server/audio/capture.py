"""Capture the Windows shared system mix through WASAPI loopback.

The callback only copies native PCM bytes into a bounded queue. Consumers can
read chunks at their own pace; a slow consumer drops the oldest queued audio
instead of allowing latency and memory use to grow without bound.
"""

from __future__ import annotations

import queue
import threading
import time
from collections.abc import Iterator
from dataclasses import dataclass


@dataclass(frozen=True)
class AudioFormat:
    sample_rate: int
    channels: int
    sample_width_bytes: int
    encoding: str = "float32"


@dataclass(frozen=True)
class AudioChunk:
    """Interleaved little-endian float32 PCM and its capture timestamp."""

    data: bytes
    frames: int
    captured_at: float
    status_flags: int


class WasapiLoopbackCapture:
    """Capture the current default Windows output using WASAPI loopback."""

    def __init__(self, *, queue_chunks: int = 8, frames_per_buffer: int = 480):
        if queue_chunks < 1:
            raise ValueError("queue_chunks must be at least 1")
        if frames_per_buffer < 1:
            raise ValueError("frames_per_buffer must be at least 1")

        self._queue: queue.Queue[AudioChunk] = queue.Queue(maxsize=queue_chunks)
        self._frames_per_buffer = frames_per_buffer
        self._audio = None
        self._stream = None
        self._stopped = threading.Event()
        self._callback_status_events = 0
        self._dropped_chunks = 0
        self._device_name = ""
        self.format: AudioFormat | None = None

    @property
    def callback_status_events(self) -> int:
        """Number of callback invocations carrying a nonzero PortAudio status flag."""
        return self._callback_status_events

    @property
    def dropped_chunks(self) -> int:
        """Number of stale queued chunks discarded to bound capture latency."""
        return self._dropped_chunks

    @property
    def device_name(self) -> str:
        return self._device_name

    def start(self) -> WasapiLoopbackCapture:
        if self._stream is not None:
            raise RuntimeError("capture is already started")

        try:
            import pyaudiowpatch as pyaudio
        except ImportError as exc:
            raise RuntimeError(
                "PyAudioWPatch is required; install it with "
                "'python -m pip install -r requirements.txt'"
            ) from exc

        self._audio = pyaudio.PyAudio()
        try:
            device = self._audio.get_default_wasapi_loopback()
            if device is None:
                raise RuntimeError(
                    "No default WASAPI loopback device was found. "
                    "Run 'python -m pyaudiowpatch' to inspect audio devices."
                )

            channels = min(2, int(device["maxInputChannels"]))
            if channels < 1:
                raise RuntimeError(f"Loopback device has no input channels: {device}")
            sample_rate = round(float(device["defaultSampleRate"]))
            if sample_rate < 1:
                raise RuntimeError(f"Invalid default sample rate for device: {device}")

            self.format = AudioFormat(sample_rate, channels, 4)
            self._device_name = str(device["name"])
            self._stopped.clear()
            self._stream = self._audio.open(
                format=pyaudio.paFloat32,
                channels=channels,
                rate=sample_rate,
                input=True,
                input_device_index=int(device["index"]),
                frames_per_buffer=self._frames_per_buffer,
                stream_callback=self._on_audio,
                start=False,
            )
            self._stream.start_stream()
            return self
        except Exception:
            self.close()
            raise

    def _on_audio(self, in_data, frame_count, time_info, status_flags):
        # Do not log, convert, or block in a PortAudio callback.
        if status_flags:
            self._callback_status_events += 1
        if in_data:
            chunk = AudioChunk(
                bytes(in_data), int(frame_count), time.monotonic(), int(status_flags)
            )
            try:
                self._queue.put_nowait(chunk)
            except queue.Full:
                try:
                    self._queue.get_nowait()
                except queue.Empty:
                    pass
                self._dropped_chunks += 1
                try:
                    self._queue.put_nowait(chunk)
                except queue.Full:
                    self._dropped_chunks += 1

        # paContinue is zero in PortAudio's callback contract.
        return (None, 0)

    def read(self, timeout: float | None = None) -> AudioChunk:
        """Return the next captured chunk, or raise queue.Empty on timeout."""
        if self._stopped.is_set() and self._queue.empty():
            raise RuntimeError("capture is stopped")
        return self._queue.get(timeout=timeout)

    def chunks(self, timeout: float = 1.0) -> Iterator[AudioChunk]:
        """Yield chunks until capture is stopped; timeouts keep shutdown responsive."""
        while not self._stopped.is_set():
            try:
                yield self.read(timeout=timeout)
            except queue.Empty:
                continue

    def close(self) -> None:
        self._stopped.set()
        stream, self._stream = self._stream, None
        audio, self._audio = self._audio, None
        try:
            if stream is not None:
                if stream.is_active():
                    stream.stop_stream()
                stream.close()
        finally:
            if audio is not None:
                audio.terminate()

    def __enter__(self) -> WasapiLoopbackCapture:
        return self.start()

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()

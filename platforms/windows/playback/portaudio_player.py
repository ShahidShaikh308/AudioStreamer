"""Play protocol PCM through the Windows default output using PortAudio."""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import Callable

from audio_plane.buffering.packet_queue import AudioPacketQueue
from audio_plane.protocol import SampleFormat

logger = logging.getLogger(__name__)


class PortAudioPlayer:
    """Blocking-write playback worker; its queue and lifecycle are independent."""

    def __init__(self, packets: AudioPacketQueue, *, frames_per_buffer: int = 480):
        if frames_per_buffer <= 0:
            raise ValueError("frames_per_buffer must be positive")
        self._packets = packets
        self._frames_per_buffer = frames_per_buffer

    def run(
        self,
        stop_event: threading.Event,
        on_error: Callable[[BaseException], None] | None = None,
    ) -> None:
        try:
            import pyaudiowpatch as pyaudio
        except ImportError as error:
            failure = RuntimeError("PyAudioWPatch is required for Windows playback")
            logger.exception("playback_dependency_error")
            if on_error is not None:
                on_error(failure)
            return

        audio = pyaudio.PyAudio()
        stream = None
        sample_rate = 0
        retry_delay = 0.25
        error_count = 0
        last_error_notice = 0.0
        try:
            logger.info("playback_worker_start")
            while not stop_event.is_set():
                packet = self._packets.take(timeout=0.25)
                if packet is None:
                    continue
                if packet.sample_format is not SampleFormat.FLOAT32_LE:
                    logger.error("playback_unsupported_format format=%s", packet.sample_format)
                    continue

                try:
                    if stream is None or sample_rate != packet.sample_rate:
                        if stream is not None:
                            self._close_stream(stream)
                        stream = audio.open(
                            format=pyaudio.paFloat32,
                            channels=packet.channels,
                            rate=packet.sample_rate,
                            output=True,
                            frames_per_buffer=self._frames_per_buffer,
                        )
                        sample_rate = packet.sample_rate
                        logger.info(
                            "playback_stream_started sample_rate=%d channels=%d",
                            sample_rate, packet.channels,
                        )
                    stream.write(packet.payload, exception_on_underflow=False)
                    retry_delay = 0.25
                except Exception as error:
                    error_count += 1
                    now = time.monotonic()
                    if error_count == 1 or now - last_error_notice >= 5.0:
                        logger.exception("playback_error sample_rate=%d count=%d", packet.sample_rate, error_count)
                        if on_error is not None:
                            on_error(error)
                        last_error_notice = now
                    if stream is not None:
                        self._close_stream(stream)
                        stream = None
                        sample_rate = 0
                    stop_event.wait(retry_delay)
                    retry_delay = min(retry_delay * 2, 5.0)
        finally:
            if stream is not None:
                self._close_stream(stream)
            try:
                audio.terminate()
            except Exception:
                logger.warning("playback_audio_terminate_error", exc_info=True)
            logger.info("playback_worker_stop")

    @staticmethod
    def _close_stream(stream) -> None:
        try:
            if stream.is_active():
                stream.stop_stream()
        except Exception:
            logger.debug("playback_stream_stop_error", exc_info=True)
        try:
            stream.close()
        except Exception:
            logger.debug("playback_stream_close_error", exc_info=True)

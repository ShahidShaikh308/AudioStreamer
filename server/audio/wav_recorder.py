"""Write captured float32 loopback chunks as a standard 48 kHz stereo PCM WAV."""

from __future__ import annotations

import argparse
import math
import queue
import struct
import time
import wave
from pathlib import Path

from .capture import AudioChunk, AudioFormat, WasapiLoopbackCapture


class WavRecorder:
    """Convert captured float32 PCM to signed 16-bit PCM and write a WAV file.

    The output is deliberately fixed at 48 kHz stereo to match this project's
    current endpoint. Conversion from float32 to int16 is lossy; use the
    captured chunks directly for any future bit-exact comparison.
    """

    SAMPLE_RATE = 48_000
    CHANNELS = 2
    SAMPLE_WIDTH = 2

    def __init__(self, path: str | Path, audio_format: AudioFormat):
        if audio_format.sample_rate != self.SAMPLE_RATE:
            raise ValueError(
                f"WAV recorder requires {self.SAMPLE_RATE} Hz input, "
                f"got {audio_format.sample_rate} Hz"
            )
        if audio_format.channels != self.CHANNELS:
            raise ValueError(
                f"WAV recorder requires {self.CHANNELS}-channel input, "
                f"got {audio_format.channels} channels"
            )
        if audio_format.encoding != "float32" or audio_format.sample_width_bytes != 4:
            raise ValueError("WAV recorder expects float32 captured PCM")

        self.path = Path(path)
        self._wav: wave.Wave_write | None = None
        self.frames_written = 0

    def __enter__(self) -> WavRecorder:
        if self._wav is not None:
            raise RuntimeError("recorder is already open")
        self._wav = wave.open(str(self.path), "wb")
        self._wav.setnchannels(self.CHANNELS)
        self._wav.setsampwidth(self.SAMPLE_WIDTH)
        self._wav.setframerate(self.SAMPLE_RATE)
        self._wav.setcomptype("NONE", "not compressed")
        return self

    def write_chunk(self, chunk: AudioChunk) -> None:
        """Convert one interleaved float32 capture chunk and append it to the WAV."""
        if self._wav is None:
            raise RuntimeError("recorder is not open")

        expected_bytes = chunk.frames * self.CHANNELS * 4
        if len(chunk.data) != expected_bytes:
            raise ValueError(
                f"chunk length mismatch: {len(chunk.data)} bytes for "
                f"{chunk.frames} stereo float32 frames (expected {expected_bytes})"
            )
        sample_count = chunk.frames * self.CHANNELS
        samples = struct.unpack(f"<{sample_count}f", chunk.data)
        pcm16 = bytearray(sample_count * self.SAMPLE_WIDTH)
        for index, sample in enumerate(samples):
            if not math.isfinite(sample):
                sample = 0.0
            # Signed PCM has one additional negative value: [-32768, 32767].
            sample = min(1.0, max(-1.0, sample))
            value = round(sample * (32768 if sample < 0 else 32767))
            struct.pack_into("<h", pcm16, index * 2, value)

        self._wav.writeframesraw(pcm16)
        self.frames_written += chunk.frames

    def close(self) -> None:
        wav_file, self._wav = self._wav, None
        if wav_file is not None:
            wav_file.close()

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()


def record_loopback(path: str | Path, seconds: float = 10.0) -> int:
    """Record the default WASAPI loopback endpoint for a fixed duration."""
    if seconds <= 0:
        raise ValueError("seconds must be positive")

    with WasapiLoopbackCapture() as capture:
        assert capture.format is not None
        with WavRecorder(path, capture.format) as recorder:
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                try:
                    chunk = capture.read(timeout=min(1.0, max(0.01, remaining)))
                except queue.Empty:
                    continue
                recorder.write_chunk(chunk)

            frames_written = recorder.frames_written

    print(f"Saved {frames_written} frames to {Path(path).resolve()}")
    if frames_written == 0:
        print("No audio frames arrived; play system audio during recording and retry.")
    else:
        print(f"Duration: {frames_written / WavRecorder.SAMPLE_RATE:.3f} seconds")
        print("Format: 48 kHz, stereo, signed 16-bit PCM WAV")
        print("Note: float32-to-int16 conversion is lossy; this file is for listening.")
    return frames_written


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "output", nargs="?", default="loopback.wav", help="output WAV path"
    )
    parser.add_argument("--seconds", type=float, default=10.0, help="capture duration")
    args = parser.parse_args()
    record_loopback(args.output, args.seconds)


if __name__ == "__main__":
    main()

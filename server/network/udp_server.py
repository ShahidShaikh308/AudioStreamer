"""Send captured WASAPI PCM chunks to a UDP receiver."""

from __future__ import annotations

import argparse
import queue
import socket
import time

from server.audio.capture import AudioChunk, AudioFormat, WasapiLoopbackCapture
from server.network.protocol import (
    UINT32_MASK,
    AudioPacket,
    encode_packet,
    split_pcm_payload,
)


class UdpAudioServer:
    """Packetize float32 capture chunks and send them to one UDP destination."""

    def __init__(
        self, destination_host: str = "127.0.0.1", destination_port: int = 5005
    ):
        if not 1 <= destination_port <= 65535:
            raise ValueError("destination_port must be between 1 and 65535")
        self.destination = (destination_host, destination_port)
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
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
        for payload in split_pcm_payload(chunk.data, audio_format.channels):
            packet = AudioPacket(
                sequence_number=self._next_sequence,
                timestamp_ns=(
                    capture_timestamp_ns
                    + frame_offset * 1_000_000_000 // audio_format.sample_rate
                ),
                sample_rate=audio_format.sample_rate,
                channels=audio_format.channels,
                payload=payload,
            )
            self._socket.sendto(encode_packet(packet), self.destination)
            self._next_sequence = (self._next_sequence + 1) & UINT32_MASK
            self.packets_sent += 1
            count += 1
            frame_offset += len(payload) // frame_size
        return count

    def stream_capture(self, capture: WasapiLoopbackCapture) -> None:
        """Forward chunks from an already-started capture until interrupted."""
        if capture.format is None:
            raise RuntimeError("capture must be started before streaming")

        print(
            f"Sending {capture.format.sample_rate} Hz, {capture.format.channels}-channel "
            f"float32 audio to {self.destination[0]}:{self.destination[1]}"
        )
        last_report = time.monotonic()
        while True:
            try:
                chunk = capture.read(timeout=0.5)
            except queue.Empty:
                continue
            self.send_chunk(chunk, capture.format)
            now = time.monotonic()
            if now - last_report >= 5.0:
                print(
                    f"Sent {self.packets_sent} packets; "
                    f"capture queue drops={capture.dropped_chunks}"
                )
                last_report = now

    def close(self) -> None:
        self._socket.close()

    def __enter__(self) -> UdpAudioServer:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1", help="receiver IP address")
    parser.add_argument("--port", type=int, default=5005, help="receiver UDP port")
    args = parser.parse_args()

    with (
        WasapiLoopbackCapture() as capture,
        UdpAudioServer(args.host, args.port) as sender,
    ):
        assert capture.format is not None
        print(f"Capturing from: {capture.device_name}")
        try:
            sender.stream_capture(capture)
        except KeyboardInterrupt:
            print("\nUDP audio sender stopped.")


if __name__ == "__main__":
    main()

"""Receive float32 PCM over UDP and play it through the default output device."""

from __future__ import annotations

import argparse
import socket
import sys

from server.network.enums import PacketType, SampleFormat
from server.network.protocol import (
    MAX_DATAGRAM_SIZE,
    AudioPacket,
    PacketError,
    decode_packet,
)


class FloatAudioPlayer:
    """Lazily open a PortAudio output stream matching the first received packet."""

    def __init__(self):
        self._audio = None
        self._stream = None
        self._format: tuple[int, int] | None = None

    def play(self, packet: AudioPacket) -> None:
        if packet.packet_type != PacketType.AUDIO:
            raise PacketError("only audio packets can be played")
        if packet.sample_format != SampleFormat.FLOAT32_LE:
            raise PacketError(
                f"Python receiver does not support sample format {packet.sample_format}"
            )
        audio_format = (packet.sample_rate, packet.channels)
        if self._format is None:
            try:
                import pyaudiowpatch as pyaudio
            except ImportError as exc:
                raise RuntimeError(
                    "PyAudioWPatch is required for playback; install requirements.txt"
                ) from exc

            self._audio = pyaudio.PyAudio()
            try:
                self._stream = self._audio.open(
                    format=pyaudio.paFloat32,
                    channels=packet.channels,
                    rate=packet.sample_rate,
                    output=True,
                    frames_per_buffer=256,
                )
            except Exception:
                self._audio.terminate()
                self._audio = None
                raise
            self._format = audio_format
            print(
                f"Playing {packet.sample_rate} Hz, {packet.channels}-channel float32 audio"
            )
        elif audio_format != self._format:
            raise PacketError(
                f"audio format changed mid-stream from {self._format} to {audio_format}"
            )

        assert self._stream is not None
        frames = len(packet.payload) // (packet.channels * 4)
        self._stream.write(
            packet.payload,
            num_frames=frames,
            exception_on_underflow=False,
        )

    def close(self) -> None:
        stream, self._stream = self._stream, None
        audio, self._audio = self._audio, None
        self._format = None
        try:
            if stream is not None:
                stream.stop_stream()
                stream.close()
        finally:
            if audio is not None:
                audio.terminate()

    def __enter__(self) -> FloatAudioPlayer:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()


class UdpAudioReceiver:
    """Receive, validate, and play protocol packets from a UDP socket."""

    def __init__(self, bind_host: str = "0.0.0.0", port: int = 5005):
        if not 0 <= port <= 65535:
            raise ValueError("port must be between 0 and 65535")
        self.bind_address = (bind_host, port)
        self.packets_received = 0
        self.invalid_packets = 0
        self.packets_lost = 0
        self._stream_id: int | None = None
        self._last_sequence: int | None = None

    def serve_forever(self) -> None:
        """Bind the UDP port and stream valid packets to the default speakers."""
        with (
            socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp,
            FloatAudioPlayer() as player,
        ):
            udp.bind(self.bind_address)
            udp.settimeout(0.5)
            print(
                f"Listening for UDP audio on {self.bind_address[0]}:{udp.getsockname()[1]}"
            )
            while True:
                try:
                    datagram, _source = udp.recvfrom(MAX_DATAGRAM_SIZE + 1)
                except TimeoutError:
                    continue

                try:
                    packet = decode_packet(datagram)
                except PacketError as exc:
                    self.invalid_packets += 1
                    print(f"Ignoring invalid UDP audio packet: {exc}", file=sys.stderr)
                    continue

                if not self._accept_sequence(packet.sequence_number, packet.stream_id):
                    continue
                if packet.packet_type != PacketType.AUDIO:
                    continue
                if packet.sample_format != SampleFormat.FLOAT32_LE:
                    self.invalid_packets += 1
                    print(
                        f"Ignoring unsupported sample format: {packet.sample_format}",
                        file=sys.stderr,
                    )
                    continue
                player.play(packet)
                self.packets_received += 1
                if self.packets_received % 1000 == 0:
                    print(
                        f"Received {self.packets_received} packets; "
                        f"lost={self.packets_lost}, invalid={self.invalid_packets}"
                    )

    def _accept_sequence(self, sequence_number: int, stream_id: int = 1) -> bool:
        if stream_id != self._stream_id:
            self._stream_id = stream_id
            self._last_sequence = sequence_number
            return True
        if self._last_sequence is None:
            self._last_sequence = sequence_number
            return True

        delta = (sequence_number - self._last_sequence) & 0xFFFF_FFFF
        if delta == 0 or delta >= 0x8000_0000:
            return False  # duplicate or an old/reordered packet
        if delta > 1:
            self.packets_lost += delta - 1
        self._last_sequence = sequence_number
        return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--bind", default="0.0.0.0", help="local interface to listen on"
    )
    parser.add_argument("--port", type=int, default=5005, help="UDP port")
    args = parser.parse_args()
    receiver = UdpAudioReceiver(args.bind, args.port)
    try:
        receiver.serve_forever()
    except KeyboardInterrupt:
        print(
            f"\nReceiver stopped: packets={receiver.packets_received}, "
            f"lost={receiver.packets_lost}, invalid={receiver.invalid_packets}"
        )


if __name__ == "__main__":
    main()

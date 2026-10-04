"""Packetize and send PCM buffers over UDP."""

from __future__ import annotations

import secrets
import socket

from audio_plane.models import PcmChunk, PcmFormat
from audio_plane.protocol import (
    UINT32_MASK,
    AudioPacket,
    SampleFormat,
    iter_pcm_payloads,
    serialize_packet,
)

class UdpAudioSender:
    """Packetize PCM chunks and send them to one UDP destination."""

    def __init__(
        self,
        destination_host: str = "127.0.0.1",
        destination_port: int = 5005,
    ):
        if not 1 <= destination_port <= 65535:
            raise ValueError("destination_port must be between 1 and 65535")
        self.destination = (destination_host, destination_port)
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.stream_id = secrets.randbits(64) or 1
        self._next_sequence = 0
        self.packets_sent = 0
        self.bytes_sent = 0

    def send_chunk(self, chunk: PcmChunk, audio_format: PcmFormat) -> int:
        """Split and send one capture chunk; return the number of datagrams sent."""
        if audio_format.encoding != "float32" or audio_format.sample_width_bytes != 4:
            raise ValueError("UDP protocol currently supports float32 capture only")
        if audio_format.sample_rate < 1 or audio_format.channels != 2:
            raise ValueError(
                "protocol v3 requires a positive sample rate and stereo capture"
            )
        expected = (
            chunk.frames * audio_format.channels * audio_format.sample_width_bytes
        )
        if len(chunk.data) != expected:
            raise ValueError(
                f"capture buffer has {len(chunk.data)} bytes; expected {expected}"
            )

        count = 0
        for payload in iter_pcm_payloads(chunk.data, audio_format.channels):
            packet = AudioPacket(
                sequence_number=self._next_sequence,
                stream_id=self.stream_id,
                sample_rate=audio_format.sample_rate,
                channels=audio_format.channels,
                sample_format=SampleFormat.FLOAT32_LE,
                payload=payload,
            )
            datagram = serialize_packet(packet)
            self._socket.sendto(datagram, self.destination)
            self._next_sequence = (self._next_sequence + 1) & UINT32_MASK
            self.packets_sent += 1
            self.bytes_sent += len(datagram)
            count += 1
        return count

    def close(self) -> None:
        self._socket.close()

    def __enter__(self) -> UdpAudioSender:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()

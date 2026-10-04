"""Windows composition root: bind platform implementations to node contracts."""

from __future__ import annotations

from audio_plane.buffering.packet_queue import AudioPacketQueue
from audio_plane.transport.udp_receiver import UdpAudioReceiver
from audio_plane.transport.udp_sender import UdpAudioSender
from platforms.windows.capture.wasapi_loopback import WasapiLoopbackCapture
from platforms.windows.desktop.config import WindowsNodeConfig
from platforms.windows.desktop.node import WindowsAudioNode
from platforms.windows.playback.portaudio_player import PortAudioPlayer


def create_windows_node(config: WindowsNodeConfig | None = None) -> WindowsAudioNode:
    """Construct the Windows node and inject its capture/network/audio adapters."""
    node_config = config or WindowsNodeConfig.load()
    node_config.validate()
    return WindowsAudioNode(
        node_config,
        capture_factory=lambda cfg: WasapiLoopbackCapture(
            queue_chunks=cfg.capture_queue_chunks,
            frames_per_buffer=cfg.frames_per_buffer,
        ),
        sender_factory=lambda cfg: UdpAudioSender(cfg.receiver_ip, cfg.receiver_port),
        receiver_factory=lambda port: UdpAudioReceiver(port),
        queue_factory=lambda: AudioPacketQueue(target_buffer_ms=80),
        player_factory=lambda packets: PortAudioPlayer(packets),
    )

"""Coordinate configuration, logging, WASAPI capture, and UDP streaming."""

from __future__ import annotations

import logging
from collections.abc import Callable
from logging.handlers import RotatingFileHandler
from threading import Event

from server.audio.capture import WasapiLoopbackCapture
from server.config import PROJECT_ROOT, ServerConfig
from server.network.udp_server import UdpAudioServer


def configure_logging(level: str) -> None:
    log_directory = PROJECT_ROOT / "logs"
    log_directory.mkdir(exist_ok=True)
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s")
    console = logging.StreamHandler()
    console.setFormatter(formatter)
    file_handler = RotatingFileHandler(
        log_directory / "audiostreamer.log",
        maxBytes=1_000_000,
        backupCount=3,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    logging.basicConfig(
        level=getattr(logging, level.upper()),
        handlers=(console, file_handler),
        force=True,
    )


def run(
    config: ServerConfig | None = None,
    *,
    stop_event: Event | None = None,
    on_started: Callable[[str, int, int], None] | None = None,
    on_stats: Callable[[int, int], None] | None = None,
) -> None:
    config = config or ServerConfig.load()
    config.validate()
    configure_logging(config.log_level)
    logger = logging.getLogger("server")

    try:
        with (
            WasapiLoopbackCapture(
                queue_chunks=config.capture_queue_chunks,
                frames_per_buffer=config.frames_per_buffer,
            ) as capture,
            UdpAudioServer(
                config.receiver_ip,
                config.receiver_port,
                config.protocol_version,
            ) as sender,
        ):
            assert capture.format is not None
            logger.info(
                "AudioStreamer v0.4\n"
                "  Capture Device: %s\n"
                "  Sample Rate: %d Hz\n"
                "  Channels: %d\n"
                "  Receiver: %s:%d\n"
                "  Protocol: v%d\n"
                "  Streaming... (Ctrl+C to stop)",
                capture.device_name,
                capture.format.sample_rate,
                capture.format.channels,
                config.receiver_ip,
                config.receiver_port,
                config.protocol_version,
            )
            if on_started is not None:
                on_started(
                    capture.device_name,
                    capture.format.sample_rate,
                    capture.format.channels,
                )
            sender.stream_capture(
                capture,
                stop_event=stop_event,
                on_stats=on_stats,
            )
    except KeyboardInterrupt:
        logger.info("AudioStreamer stopped by user")

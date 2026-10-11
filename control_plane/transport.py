"""Windows local Control Plane client transport; UDP remains broker-owned."""

from __future__ import annotations

import logging
import os
import subprocess
import sys
import threading
import time
from collections.abc import Callable
from multiprocessing.connection import Client, Connection

from control_plane.broker import AUTH_KEY, PIPE_NAME
from control_plane.config import PROJECT_ROOT

logger = logging.getLogger(__name__)
DatagramHandler = Callable[[bytes, tuple[str, int]], None]


class ControlTransportStartupError(OSError):
    """The shared broker could not start or the local client could not attach."""


class ControlTransportClient:
    """Connects a manager to the broker's authenticated local named-pipe bus."""

    def __init__(self, port: int, on_datagram: DatagramHandler,
                 on_error: Callable[[OSError], None],
                 on_reconnected: Callable[[], None] | None = None) -> None:
        self.port = port
        self._on_datagram = on_datagram
        self._on_error = on_error
        self._on_reconnected = on_reconnected
        self._broker_process: subprocess.Popen[bytes] | None = None
        self._connection: Connection | None = None
        self._connection_lock = threading.RLock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        self._connect_or_start()
        self._thread = threading.Thread(target=self._receive_loop,
                                        name="AudioStreamer-ControlPipe", daemon=True)
        self._thread.start()

    def send(self, data: bytes, destination: tuple[str, int]) -> None:
        with self._connection_lock:
            connection = self._connection
            if connection is None:
                raise OSError("Control Plane broker is disconnected")
            try:
                connection.send(("send", data, destination))
            except (OSError, EOFError, BrokenPipeError) as error:
                self._connection = None
                try:
                    connection.close()
                except OSError:
                    pass
                raise OSError("Control Plane broker connection failed") from error

    def close(self) -> None:
        self._stop.set()
        with self._connection_lock:
            connection, self._connection = self._connection, None
            if connection is not None:
                try:
                    connection.send(("close",))
                except (OSError, EOFError, BrokenPipeError):
                    pass
                try:
                    connection.close()
                except OSError:
                    pass
        if self._thread is not None and self._thread is not threading.current_thread():
            self._thread.join(timeout=1.0)

    def _connect_or_start(self) -> None:
        try:
            self._connect_once()
            return
        except OSError:
            pass
        self._start_broker()
        deadline = time.monotonic() + 6.0
        last_error: OSError | None = None
        while time.monotonic() < deadline and not self._stop.is_set():
            try:
                self._connect_once()
                return
            except OSError as error:
                last_error = error
                time.sleep(0.1)
        raise ControlTransportStartupError(self._broker_failure_reason(last_error))

    def _connect_once(self) -> None:
        if os.name != "nt":
            raise OSError("The shared named-pipe Control Plane broker requires Windows")
        connection = Client(PIPE_NAME, family="AF_PIPE", authkey=AUTH_KEY)
        try:
            response = connection.recv()
            if response != ("ready", self.port):
                raise OSError(
                    f"Control Plane broker uses port {response[1] if isinstance(response, tuple) and len(response) == 2 else 'unknown'}, "
                    f"but this client requires {self.port}"
                )
        except Exception:
            connection.close()
            raise
        with self._connection_lock:
            self._connection = connection

    def _start_broker(self) -> None:
        if self._broker_process is not None and self._broker_process.poll() is None:
            return
        command = [sys.executable, "-m", "control_plane.broker", "--port", str(self.port)]
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
        try:
            self._broker_process = subprocess.Popen(
                command, cwd=PROJECT_ROOT, stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                creationflags=flags, close_fds=True,
            )
        except OSError as error:
            raise ControlTransportStartupError(self._broker_failure_reason(error)) from error

    def _broker_failure_reason(self, error: OSError | None) -> str:
        detail = str(error) if error is not None else "no broker response"
        return f"could not connect to the named-pipe broker for UDP port {self.port}: {detail}"

    def _receive_loop(self) -> None:
        while not self._stop.is_set():
            with self._connection_lock:
                connection = self._connection
            if connection is None:
                try:
                    self._connect_or_start()
                    if self._on_reconnected is not None:
                        self._on_reconnected()
                except OSError as error:
                    self._on_error(error)
                    self._stop.wait(1.0)
                    continue
                continue
            try:
                message = connection.recv()
                if (isinstance(message, tuple) and len(message) == 3
                        and message[0] == "datagram" and isinstance(message[1], bytes)):
                    self._on_datagram(message[1], message[2])
            except (OSError, EOFError, BrokenPipeError) as error:
                with self._connection_lock:
                    if self._connection is connection:
                        self._connection = None
                try:
                    connection.close()
                except OSError:
                    pass
                if not self._stop.is_set():
                    self._on_error(OSError(f"Control Plane broker disconnected: {error}"))


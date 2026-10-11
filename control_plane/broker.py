"""Single-owner Windows UDP broker with a local authenticated named-pipe bus."""

from __future__ import annotations

import argparse
import logging
import queue
import socket
import threading
from logging.handlers import RotatingFileHandler
from multiprocessing.connection import Connection, Listener

from control_plane.config import PROJECT_ROOT, load_control_port

logger = logging.getLogger(__name__)
PIPE_NAME = r"\\.\pipe\AudioStreamer.ControlPlane.v1"
AUTH_KEY = b"AudioStreamer.ControlPlane.Local.v1"
MAX_DATAGRAM = 1200
CLIENT_QUEUE_SIZE = 256


class BrokerClient:
    """One local control manager connection and its bounded datagram queue."""

    def __init__(self, server: ControlBroker, connection: Connection) -> None:
        self.server = server
        self.connection = connection
        self.closed = threading.Event()
        self.outbound: queue.Queue[tuple[bytes, tuple[str, int]]] = queue.Queue(CLIENT_QUEUE_SIZE)
        self.send_lock = threading.Lock()
        self.writer = threading.Thread(target=self.write_loop, name="ControlBroker-PipeWriter", daemon=True)
        self.reader = threading.Thread(target=self.read_loop, name="ControlBroker-PipeReader", daemon=True)
        self.dropped_datagrams = 0

    def start(self) -> None:
        try:
            self.connection.send(("ready", self.server.port))
        except (OSError, EOFError, BrokenPipeError):
            self.close()
            return
        self.writer.start()
        self.reader.start()

    def offer(self, message: tuple[bytes, tuple[str, int]]) -> None:
        if self.closed.is_set():
            return
        try:
            self.outbound.put_nowait(message)
        except queue.Full:
            try:
                self.outbound.get_nowait()
            except queue.Empty:
                pass
            self.dropped_datagrams += 1
            try:
                self.outbound.put_nowait(message)
            except queue.Full:
                self.dropped_datagrams += 1

    def write_loop(self) -> None:
        while not self.closed.is_set():
            try:
                message = self.outbound.get(timeout=0.25)
            except queue.Empty:
                continue
            try:
                with self.send_lock:
                    self.connection.send(("datagram", message[0], message[1]))
            except (OSError, EOFError, BrokenPipeError):
                self.close()

    def read_loop(self) -> None:
        try:
            while not self.closed.is_set():
                message = self.connection.recv()
                if not isinstance(message, tuple) or not message:
                    continue
                if message[0] == "close":
                    break
                if message[0] == "send" and len(message) == 3:
                    payload, destination = message[1], message[2]
                    if isinstance(payload, bytes) and len(payload) <= MAX_DATAGRAM:
                        self.server.send_udp(payload, destination)
        except (OSError, EOFError):
            pass
        finally:
            self.close()

    def close(self) -> None:
        if self.closed.is_set():
            return
        self.closed.set()
        try:
            self.connection.close()
        except OSError:
            pass
        self.server.remove_client(self)


class ControlBroker:
    """Owns the only Windows Control Plane UDP socket and relays datagrams."""

    def __init__(self, port: int) -> None:
        if not 49152 <= port <= 65535:
            raise ValueError("Control Plane port must be between 49152 and 65535")
        self.port = port
        self._clients: set[BrokerClient] = set()
        self._clients_lock = threading.RLock()
        self._socket: socket.socket | None = None
        self._socket_lock = threading.Lock()
        self._stopping = threading.Event()

    def serve_forever(self) -> None:
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        udp.bind(("0.0.0.0", self.port))
        udp.settimeout(0.25)
        self._socket = udp
        listener = Listener(PIPE_NAME, family="AF_PIPE", authkey=AUTH_KEY)
        threading.Thread(target=self._udp_receive_loop, name="ControlBroker-UDP", daemon=True).start()
        logger.info("control_broker_listening port=%d", self.port)
        try:
            while not self._stopping.is_set():
                try:
                    connection = listener.accept()
                except OSError:
                    if self._stopping.is_set():
                        break
                    raise
                client = BrokerClient(self, connection)
                with self._clients_lock:
                    self._clients.add(client)
                client.start()
        finally:
            self._stopping.set()
            listener.close()
            with self._clients_lock:
                clients = tuple(self._clients)
            for client in clients:
                client.close()
            udp.close()
            self._socket = None

    @property
    def client_count(self) -> int:
        with self._clients_lock:
            return len(self._clients)

    def remove_client(self, client: BrokerClient) -> None:
        with self._clients_lock:
            self._clients.discard(client)

    def send_udp(self, payload: bytes, destination: tuple[str, int]) -> None:
        with self._socket_lock:
            udp = self._socket
            if udp is not None and not self._stopping.is_set():
                udp.sendto(payload, destination)

    def _udp_receive_loop(self) -> None:
        while not self._stopping.is_set():
            udp = self._socket
            if udp is None:
                return
            try:
                payload, address = udp.recvfrom(MAX_DATAGRAM + 1)
            except socket.timeout:
                continue
            except OSError:
                if not self._stopping.is_set():
                    logger.exception("control_broker_receive_error")
                return
            if len(payload) > MAX_DATAGRAM:
                continue
            with self._clients_lock:
                clients = tuple(self._clients)
            for client in clients:
                client.offer((payload, address))


def configure_broker_logging() -> None:
    directory = PROJECT_ROOT / "logs"
    directory.mkdir(exist_ok=True)
    handler = RotatingFileHandler(directory / "control_broker.log", maxBytes=1_000_000,
                                  backupCount=3, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=(handler,), force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=load_control_port())
    arguments = parser.parse_args()
    configure_broker_logging()
    try:
        ControlBroker(arguments.port).serve_forever()
    except Exception:
        logger.exception("control_broker_startup_failed")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""Thread-safe peer registry; it stores announced data and no sockets."""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, replace

from control_plane.protocol import Capability, NodeStatus


@dataclass(frozen=True, slots=True)
class NodeRecord:
    node_id: int
    name: str
    platform: str
    capabilities: Capability
    address: str
    receive_port: int
    status: NodeStatus
    last_seen: float


class NodeRegistry:
    """Owns an immutable snapshot of peers discovered on the Control Plane."""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._nodes: dict[int, NodeRecord] = {}

    def get(self, node_id: int) -> NodeRecord | None:
        with self._lock:
            return self._nodes.get(node_id)

    def snapshot(self) -> tuple[NodeRecord, ...]:
        with self._lock:
            return tuple(sorted(self._nodes.values(), key=lambda node: node.node_id))

    def upsert(self, node: NodeRecord) -> tuple[NodeRecord, bool]:
        with self._lock:
            added = node.node_id not in self._nodes
            self._nodes[node.node_id] = node
            return node, added

    def heartbeat(
        self, node_id: int, address: str, status: NodeStatus, now: float | None = None
    ) -> NodeRecord | None:
        with self._lock:
            old = self._nodes.get(node_id)
            if old is None:
                return None
            updated = replace(
                old,
                address=address,
                status=status,
                last_seen=time.monotonic() if now is None else now,
            )
            self._nodes[node_id] = updated
            return updated

    def remove(self, node_id: int) -> NodeRecord | None:
        with self._lock:
            return self._nodes.pop(node_id, None)

    def expire(self, timeout_seconds: float, now: float | None = None) -> tuple[NodeRecord, ...]:
        current = time.monotonic() if now is None else now
        with self._lock:
            expired = tuple(
                node for node in self._nodes.values()
                if current - node.last_seen >= timeout_seconds
            )
            for node in expired:
                self._nodes.pop(node.node_id, None)
            return expired

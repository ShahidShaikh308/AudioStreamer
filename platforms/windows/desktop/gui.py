"""Lightweight control surface for independent Windows node capabilities."""

from __future__ import annotations

import logging
import queue
import time
import tkinter as tk
from tkinter import ttk

from core.node.events import EventKind, Lane, NodeEvent
from core.node.interfaces import INode, NodeSettings
from core.node.state import LaneState

logger = logging.getLogger(__name__)


class AudioStreamerWindow:
    """Tk owns UI state; capture, UDP, and playback run on node workers."""

    def __init__(self, root: tk.Tk, node: INode) -> None:
        self.root = root
        self.root.title("AudioStreamer Windows Node v1")
        self.root.minsize(650, 550)
        self._events: queue.Queue[NodeEvent] = queue.Queue()
        self._closing = False
        self._last_packet_at = 0.0
        self._last_error = "None"
        self._last_capture_warning: tuple[int, int] = (0, 0)

        self.node = node
        self._unsubscribe = self.node.subscribe(self._events.put)
        settings = node.settings
        identity = node.identity
        self.node_id = identity.node_id
        self.control_port = identity.control_port
        logger.info(
            "windows_node_startup device=%s local_ip=%s destination=%s:%d receive_port=%d protocol=v%d",
            identity.device_name,
            identity.local_address,
            settings.destination_ip,
            settings.destination_port,
            settings.receive_port,
            identity.protocol_version,
        )

        self.device_name = tk.StringVar(value=settings.device_name)
        self.local_ip = tk.StringVar(value=identity.local_address)
        self.protocol = tk.StringVar(
            value=f"AudioStreamer v{identity.protocol_version}"
        )
        self.control_status = tk.StringVar(value="Discovering peers")
        self.destination_ip = tk.StringVar(value=settings.destination_ip)
        self.destination_port = tk.StringVar(value=str(settings.destination_port))
        self.receive_port = tk.StringVar(value=str(settings.receive_port))
        self.sender_status = tk.StringVar(value="Stopped")
        self.receiver_status = tk.StringVar(value="Stopped")
        self.packets_sent = tk.StringVar(value="0")
        self.bytes_sent = tk.StringVar(value="0")
        self.packets_received = tk.StringVar(value="0")
        self.bytes_received = tk.StringVar(value="0")
        self.sample_format = tk.StringVar(value="—")
        self.latency = tk.StringVar(value="Unavailable (protocol v3 has no timestamps)")
        self.queue_status = tk.StringVar(value="0 ms; 0 overflow drops")
        self.error_status = tk.StringVar(value=self._last_error)

        self._build_widgets()
        self._refresh_known_nodes(node.known_nodes)
        self.root.protocol("WM_DELETE_WINDOW", self._close_window)
        self.root.after(200, self._process_events)

    def _build_widgets(self) -> None:
        body = ttk.Frame(self.root, padding=16)
        body.grid(row=0, column=0, sticky="nsew")
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        body.columnconfigure(1, weight=1)

        ttk.Label(body, text="AudioStreamer Node", font=("Segoe UI", 16, "bold")).grid(
            row=0, column=0, columnspan=3, sticky="w", pady=(0, 10)
        )
        ttk.Label(body, text="Local device").grid(row=1, column=0, sticky="w", pady=3)
        ttk.Entry(body, textvariable=self.device_name).grid(
            row=1, column=1, sticky="ew", pady=3
        )
        ttk.Label(body, textvariable=self.protocol).grid(
            row=1, column=2, sticky="e", padx=(12, 0)
        )
        ttk.Label(body, text="Local IP").grid(row=2, column=0, sticky="w", pady=3)
        ttk.Label(body, textvariable=self.local_ip).grid(
            row=2, column=1, sticky="w", pady=3
        )
        ttk.Label(body, text=f"Node ID {self.node_id}").grid(
            row=2, column=2, sticky="e", padx=(12, 0), pady=3
        )

        settings = ttk.LabelFrame(body, text="Node settings", padding=10)
        settings.grid(row=3, column=0, columnspan=3, sticky="ew", pady=(10, 8))
        settings.columnconfigure(1, weight=1)
        ttk.Label(settings, text="Destination IP").grid(
            row=0, column=0, sticky="w", pady=3
        )
        ttk.Entry(settings, textvariable=self.destination_ip).grid(
            row=0, column=1, sticky="ew", pady=3
        )
        ttk.Label(settings, text="Destination port").grid(
            row=1, column=0, sticky="w", pady=3
        )
        ttk.Entry(settings, textvariable=self.destination_port, width=9).grid(
            row=1, column=1, sticky="w", pady=3
        )
        ttk.Label(settings, text="Receive port").grid(
            row=2, column=0, sticky="w", pady=3
        )
        ttk.Entry(settings, textvariable=self.receive_port, width=9).grid(
            row=2, column=1, sticky="w", pady=3
        )
        ttk.Button(settings, text="Save Configuration", command=self._save_config).grid(
            row=0, column=2, rowspan=3, padx=(12, 0), sticky="ns"
        )

        sender = ttk.LabelFrame(body, text="Audio sender", padding=10)
        sender.grid(row=4, column=0, columnspan=3, sticky="ew", pady=5)
        sender.columnconfigure(1, weight=1)
        self.sender_start_button = ttk.Button(
            sender, text="Start Sender", command=self._start_sender
        )
        self.sender_start_button.grid(row=0, column=0, padx=(0, 8))
        self.sender_stop_button = ttk.Button(
            sender, text="Stop Sender", command=self.node.stop_sender, state="disabled"
        )
        self.sender_stop_button.grid(row=0, column=1, sticky="w")
        ttk.Label(sender, text="Status").grid(row=1, column=0, sticky="w", pady=(8, 2))
        ttk.Label(sender, textvariable=self.sender_status).grid(
            row=1, column=1, sticky="w", pady=(8, 2)
        )
        ttk.Label(sender, text="Packets sent").grid(row=2, column=0, sticky="w", pady=2)
        ttk.Label(sender, textvariable=self.packets_sent).grid(
            row=2, column=1, sticky="w", pady=2
        )
        ttk.Label(sender, text="Bytes sent").grid(row=3, column=0, sticky="w", pady=2)
        ttk.Label(sender, textvariable=self.bytes_sent).grid(
            row=3, column=1, sticky="w", pady=2
        )

        receiver = ttk.LabelFrame(body, text="Audio receiver", padding=10)
        receiver.grid(row=5, column=0, columnspan=3, sticky="ew", pady=5)
        receiver.columnconfigure(1, weight=1)
        self.receiver_start_button = ttk.Button(
            receiver, text="Start Receiver", command=self._start_receiver
        )
        self.receiver_start_button.grid(row=0, column=0, padx=(0, 8))
        self.receiver_stop_button = ttk.Button(
            receiver,
            text="Stop Receiver",
            command=self.node.stop_receiver,
            state="disabled",
        )
        self.receiver_stop_button.grid(row=0, column=1, sticky="w")
        ttk.Label(receiver, text="Status").grid(
            row=1, column=0, sticky="w", pady=(8, 2)
        )
        ttk.Label(receiver, textvariable=self.receiver_status).grid(
            row=1, column=1, sticky="w", pady=(8, 2)
        )
        ttk.Label(receiver, text="Packets received").grid(
            row=2, column=0, sticky="w", pady=2
        )
        ttk.Label(receiver, textvariable=self.packets_received).grid(
            row=2, column=1, sticky="w", pady=2
        )
        ttk.Label(receiver, text="Bytes received").grid(
            row=3, column=0, sticky="w", pady=2
        )
        ttk.Label(receiver, textvariable=self.bytes_received).grid(
            row=3, column=1, sticky="w", pady=2
        )

        diagnostics = ttk.LabelFrame(body, text="Audio diagnostics", padding=10)
        diagnostics.grid(row=6, column=0, columnspan=3, sticky="ew", pady=5)
        diagnostics.columnconfigure(1, weight=1)
        for row, (label, value) in enumerate(
            (
                ("Current sample rate / channels", self.sample_format),
                ("Current playback queue", self.queue_status),
                ("Latency", self.latency),
                ("Last error", self.error_status),
            )
        ):
            ttk.Label(diagnostics, text=label).grid(
                row=row, column=0, sticky="nw", pady=3
            )
            ttk.Label(diagnostics, textvariable=value, wraplength=440).grid(
                row=row, column=1, columnspan=2, sticky="w", pady=3
            )

        ttk.Label(
            body,
            text="Sender and receiver run independently. UDP has no handshake or delivery confirmation.",
            foreground="#666666",
            wraplength=600,
        ).grid(row=7, column=0, columnspan=3, sticky="w", pady=(8, 0))

        topology = ttk.LabelFrame(body, text="Control Plane · Known nodes", padding=10)
        topology.grid(row=8, column=0, columnspan=3, sticky="ew", pady=(10, 4))
        topology.columnconfigure(0, weight=1)
        ttk.Label(topology, textvariable=self.control_status).grid(
            row=0, column=0, sticky="w"
        )
        self.node_table = ttk.Treeview(
            topology,
            columns=("name", "platform", "address", "status", "capabilities"),
            show="headings",
            height=4,
        )
        for column, title, width in (
            ("name", "Name", 140),
            ("platform", "Platform", 80),
            ("address", "IP · Receive port", 130),
            ("status", "Status", 85),
            ("capabilities", "Capabilities", 180),
        ):
            self.node_table.heading(column, text=title)
            self.node_table.column(
                column, width=width, stretch=column in {"name", "capabilities"}
            )
        self.node_table.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(6, 8))
        stream_controls = ttk.Frame(topology)
        stream_controls.grid(row=2, column=0, columnspan=3, sticky="ew")
        ttk.Button(
            stream_controls,
            text="Send to selected",
            command=lambda: self._request_stream(send=True),
        ).pack(side="left", padx=(0, 6))
        ttk.Button(
            stream_controls,
            text="Receive from selected",
            command=lambda: self._request_stream(send=False),
        ).pack(side="left", padx=(0, 6))
        ttk.Button(
            stream_controls,
            text="Stop selected stream",
            command=self._stop_selected_stream,
        ).pack(side="left")

    def _read_settings(self) -> NodeSettings:
        return NodeSettings(
            device_name=self.device_name.get().strip(),
            destination_ip=self.destination_ip.get().strip(),
            destination_port=int(self.destination_port.get().strip()),
            receive_port=int(self.receive_port.get().strip()),
        )

    def _save_config(self) -> None:
        try:
            self.node.update_settings(self._read_settings())
            self.node.save_configuration()
            self.local_ip.set(self.node.identity.local_address)
            self._set_error("None")
            logger.info("configuration_saved")
        except (OSError, ValueError, TypeError) as error:
            self._set_error(str(error))

    def _start_sender(self) -> None:
        try:
            self.node.update_settings(self._read_settings())
            self.local_ip.set(self.node.identity.local_address)
            self._last_capture_warning = (0, 0)
            self.packets_sent.set("0")
            self.bytes_sent.set("0")
            self._set_error("None")
            self.sender_status.set("Starting capture…")
            self.sender_start_button.configure(state="disabled")
            self.sender_stop_button.configure(state="normal")
            self.node.start_sender()
        except (OSError, ValueError, TypeError, RuntimeError) as error:
            self.sender_status.set("Error")
            self.sender_start_button.configure(state="normal")
            self.sender_stop_button.configure(state="disabled")
            self._set_error(str(error))

    def _start_receiver(self) -> None:
        try:
            self.node.update_settings(self._read_settings())
            self.local_ip.set(self.node.identity.local_address)
            self._last_packet_at = 0.0
            self.packets_received.set("0")
            self.bytes_received.set("0")
            self.queue_status.set("0 ms; 0 overflow drops")
            self._set_error("None")
            self.receiver_status.set("Starting listener…")
            self.receiver_start_button.configure(state="disabled")
            self.receiver_stop_button.configure(state="normal")
            self.node.start_receiver()
        except (OSError, ValueError, TypeError, RuntimeError) as error:
            self.receiver_status.set("Error")
            self.receiver_start_button.configure(state="normal")
            self.receiver_stop_button.configure(state="disabled")
            self._set_error(str(error))

    def _process_events(self) -> None:
        try:
            while True:
                if self._handle_event(self._events.get_nowait()):
                    return
        except queue.Empty:
            pass

        self.root.after(100, self._process_events)

    def _handle_event(self, event: NodeEvent) -> bool:
        if event.kind is EventKind.STATE_CHANGED:
            if event.lane is Lane.SENDER:
                self.sender_status.set(self._state_label(event.snapshot.sender))
                self.sender_start_button.configure(
                    state="normal"
                    if event.snapshot.sender in {LaneState.STOPPED, LaneState.ERROR}
                    else "disabled"
                )
                self.sender_stop_button.configure(
                    state="normal"
                    if event.snapshot.sender in {LaneState.STARTING, LaneState.RUNNING}
                    else "disabled"
                )
            elif event.lane is Lane.RECEIVER:
                state = event.snapshot.receiver
                self.receiver_status.set(
                    f"Listening on UDP {event.data}…"
                    if state is LaneState.STARTING and event.data
                    else self._state_label(state)
                )
                self.receiver_start_button.configure(
                    state="normal"
                    if state in {LaneState.STOPPED, LaneState.ERROR}
                    else "disabled"
                )
                self.receiver_stop_button.configure(
                    state="normal"
                    if state in {LaneState.STARTING, LaneState.RUNNING}
                    else "disabled"
                )
        elif event.kind is EventKind.FORMAT_CHANGED:
            device, rate, channels = event.data
            self.sample_format.set(f"{rate} Hz, {channels} channels — {device}")
        elif event.kind is EventKind.SENDER_STATS:
            packets, byte_count, queue_drops, callback_events = event.data
            self.packets_sent.set(str(packets))
            self.bytes_sent.set(str(byte_count))
            if (queue_drops, callback_events) != (0, 0) and (
                (queue_drops, callback_events) != self._last_capture_warning
            ):
                self._last_capture_warning = (queue_drops, callback_events)
                logger.warning(
                    "capture_diagnostics queue_drops=%d callback_status_events=%d",
                    queue_drops,
                    callback_events,
                )
        elif event.kind is EventKind.RECEIVER_STATS:
            stats = event.data
            self.packets_received.set(str(stats.packets_received))
            self.bytes_received.set(str(stats.bytes_received))
            if stats.sample_rate:
                self.sample_format.set(
                    f"{stats.sample_rate} Hz, {stats.channels} channels"
                )
            self._last_packet_at = stats.last_packet_at
            if stats.packets_received and time.monotonic() - self._last_packet_at < 2.0:
                self.receiver_status.set("Receiving audio")
            elif event.snapshot.receiver is LaneState.RUNNING:
                self.receiver_status.set("Listening; waiting for audio")
        elif event.kind is EventKind.ERROR:
            self._set_error(event.message or "Unknown node error")
            if event.lane is Lane.RECEIVER:
                self.receiver_status.set("Network or playback error; see logs")
        elif event.kind is EventKind.QUEUE_STATS:
            buffered_ms, dropped = event.data
            self.queue_status.set(f"{buffered_ms:.1f} ms; {dropped} overflow drops")
        elif event.kind is EventKind.CONTROL_TOPOLOGY:
            self._refresh_known_nodes(event.data or ())
        elif event.kind is EventKind.SHUTDOWN_COMPLETE and self._closing:
            logger.info("windows_node_shutdown_complete")
            self._unsubscribe()
            self.root.destroy()
            return True
        return False

    def _refresh_known_nodes(self, nodes) -> None:
        selected = self.node_table.selection()
        for item in self.node_table.get_children():
            self.node_table.delete(item)
        for node in nodes:
            capabilities = ", ".join(
                label
                for flag, label in (
                    (1, "Capture"),
                    (2, "Playback"),
                    (4, "Sender"),
                    (8, "Receiver"),
                )
                if int(node.capabilities) & flag
            )
            self.node_table.insert(
                "",
                "end",
                iid=f"{node.node_id:016x}",
                values=(
                    node.name,
                    node.platform,
                    f"{node.address} · {node.receive_port}",
                    node.status.name,
                    capabilities,
                ),
            )
        if selected and self.node_table.exists(selected[0]):
            self.node_table.selection_set(selected[0])
        self.control_status.set(
            f"{len(nodes)} node(s) online · Control UDP {self.control_port}"
        )

    def _selected_peer_id(self) -> int | None:
        selection = self.node_table.selection()
        return int(selection[0], 16) if selection else None

    def _request_stream(self, *, send: bool) -> None:
        peer_id = self._selected_peer_id()
        if peer_id is None:
            self._set_error("Select a discovered node first")
            return
        local_id = int(self.node_id, 16)
        try:
            self.node.start_stream(
                local_id, peer_id
            ) if send else self.node.start_stream(peer_id, local_id)
            self._set_error("None")
        except (OSError, ValueError, RuntimeError, KeyError) as error:
            self._set_error(str(error))

    def _stop_selected_stream(self) -> None:
        peer_id = self._selected_peer_id()
        if peer_id is None:
            self._set_error("Select a discovered node first")
            return
        local_id = int(self.node_id, 16)
        try:
            self.node.stop_stream(local_id, peer_id)
            self.node.stop_stream(peer_id, local_id)
            self._set_error("None")
        except (OSError, ValueError, RuntimeError, KeyError) as error:
            self._set_error(str(error))

    @staticmethod
    def _state_label(state: LaneState) -> str:
        return {
            LaneState.STOPPED: "Stopped",
            LaneState.STARTING: "Starting…",
            LaneState.RUNNING: "Running",
            LaneState.STOPPING: "Stopping…",
            LaneState.ERROR: "Error",
        }[state]

    def _set_error(self, message: str) -> None:
        self._last_error = message
        self.error_status.set(message)

    def _close_window(self) -> None:
        self._closing = True
        logger.info("windows_node_shutdown_requested")
        self.sender_start_button.configure(state="disabled")
        self.receiver_start_button.configure(state="disabled")
        self.node.shutdown()


def launch_gui(node: INode) -> None:
    root = tk.Tk()
    AudioStreamerWindow(root, node)
    root.mainloop()

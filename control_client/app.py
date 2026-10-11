"""Small Tk interface for Control Plane node and stream management."""

from __future__ import annotations

import queue
import tkinter as tk
from tkinter import messagebox, ttk

from control_plane.manager import ControlEvent, ControlEventKind
from control_plane.protocol import Capability
from control_plane.registry import NodeRecord

from .controller import DeviceManagerController
from .settings import DeviceManagerSettings, default_settings_path


class DeviceManagerWindow:
    def __init__(self, root: tk.Tk, settings: DeviceManagerSettings) -> None:
        self.root = root
        self.root.title("AudioStreamer Device Manager")
        self.root.minsize(620, 420)
        self._events: queue.Queue[ControlEvent] = queue.Queue()
        self.controller = DeviceManagerController(settings, self._events.put)
        self._peers: dict[str, NodeRecord] = {}
        self._senders: dict[str, NodeRecord] = {}
        self._receivers: dict[str, NodeRecord] = {}
        self._build()
        self.controller.start()
        self._refresh()
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(150, self._drain_events)

    def _build(self) -> None:
        body = ttk.Frame(self.root, padding=12)
        body.pack(fill="both", expand=True)
        self.status = tk.StringVar(value="Starting Control Plane…")
        self.identity = tk.StringVar(value=f"Node {self.controller.node_id:016x} · {self.controller.local_ip}")
        ttk.Label(body, textvariable=self.identity).pack(anchor="w")
        ttk.Label(body, textvariable=self.status).pack(anchor="w", pady=(4, 8))
        self.nodes = ttk.Treeview(body, columns=("platform", "ip", "capabilities", "status"), show="headings", height=9)
        for key, label, width in (("platform", "Platform", 120), ("ip", "Address", 130),
                                  ("capabilities", "Capabilities", 170), ("status", "Status", 90)):
            self.nodes.heading(key, text=label)
            self.nodes.column(key, width=width, anchor="w")
        self.nodes.pack(fill="both", expand=True)
        controls = ttk.Frame(body)
        controls.pack(fill="x", pady=(10, 0))
        ttk.Label(controls, text="Sender").grid(row=0, column=0, sticky="w")
        ttk.Label(controls, text="Receiver").grid(row=0, column=1, sticky="w")
        self.sender = ttk.Combobox(controls, state="readonly")
        self.receiver = ttk.Combobox(controls, state="readonly")
        self.sender.grid(row=1, column=0, sticky="ew", padx=(0, 6))
        self.receiver.grid(row=1, column=1, sticky="ew", padx=(0, 6))
        controls.columnconfigure(0, weight=1)
        controls.columnconfigure(1, weight=1)
        ttk.Button(controls, text="Start Stream", command=self._start_stream).grid(row=1, column=2, padx=3)
        ttk.Button(controls, text="Stop Stream", command=self._stop_stream).grid(row=1, column=3, padx=3)
        ttk.Button(controls, text="Save Settings", command=self._save).grid(row=1, column=4, padx=3)

    def _refresh(self) -> None:
        peers = self.controller.peers
        current = {self._key(peer): peer for peer in peers}
        self._peers = current
        sender_mask = Capability.CAPTURE | Capability.SENDER
        receiver_mask = Capability.PLAYBACK | Capability.RECEIVER
        self._senders = {key: peer for key, peer in current.items()
                         if peer.capabilities & sender_mask == sender_mask}
        self._receivers = {key: peer for key, peer in current.items()
                           if peer.capabilities & receiver_mask == receiver_mask}
        self.nodes.delete(*self.nodes.get_children())
        for key, peer in current.items():
            capability_names = []
            for bit, name in ((Capability.CAPTURE, "Capture"), (Capability.PLAYBACK, "Playback"),
                              (Capability.SENDER, "Sender"), (Capability.RECEIVER, "Receiver")):
                if peer.capabilities & bit:
                    capability_names.append(name)
            self.nodes.insert("", "end", iid=key, values=(peer.platform, peer.address,
                               ", ".join(capability_names) or "Control only", peer.status.name.title()))
        sender_labels = list(self._senders)
        receiver_labels = list(self._receivers)
        self.sender["values"] = sender_labels
        self.receiver["values"] = receiver_labels
        if sender_labels:
            if self.sender.get() not in self._senders: self.sender.current(0)
        else:
            self.sender.set("")
        if receiver_labels:
            if self.receiver.get() not in self._receivers: self.receiver.current(0)
        else:
            self.receiver.set("")
        self.identity.set(f"Node {self.controller.node_id:016x} · {self.controller.local_ip} · UDP {self.controller.settings.control_port}")

    @staticmethod
    def _key(peer: NodeRecord) -> str:
        return f"{peer.name} [{peer.node_id:016x}]"

    def _selected_pair(self) -> tuple[int, int] | None:
        sender = self._senders.get(self.sender.get())
        receiver = self._receivers.get(self.receiver.get())
        if sender is None or receiver is None or sender.node_id == receiver.node_id:
            messagebox.showerror("Select nodes", "Choose two different online nodes.", parent=self.root)
            return None
        return sender.node_id, receiver.node_id

    def _start_stream(self) -> None:
        pair = self._selected_pair()
        if pair:
            self._command(lambda: self.controller.start_stream(*pair))

    def _stop_stream(self) -> None:
        pair = self._selected_pair()
        if pair:
            self._command(lambda: self.controller.stop_stream(*pair))

    def _command(self, command) -> None:
        try:
            command()
            self.status.set("Control command sent; UDP has no delivery acknowledgement.")
        except Exception as error:
            messagebox.showerror("Command failed", str(error), parent=self.root)

    def _save(self) -> None:
        try:
            updated = DeviceManagerSettings(self.controller.settings.node_name,
                                            self.controller.settings.control_port,
                                            self.controller.settings.receive_port)
            self.controller.update_settings(updated)
            updated.save(default_settings_path())
            self.status.set("Settings saved")
        except Exception as error:
            messagebox.showerror("Settings error", str(error), parent=self.root)

    def _drain_events(self) -> None:
        changed = False
        while True:
            try:
                event = self._events.get_nowait()
            except queue.Empty:
                break
            changed = True
            if event.kind is ControlEventKind.ERROR:
                self.status.set(str(event.message))
            else:
                self.status.set(f"Control Plane active · {len(self.controller.peers)} peer(s)")
        if changed:
            self._refresh()
        self.root.after(150, self._drain_events)

    def close(self) -> None:
        self.controller.close()
        self.root.destroy()


def launch_device_manager() -> None:
    root = tk.Tk()
    try:
        settings = DeviceManagerSettings.load()
        DeviceManagerWindow(root, settings)
    except Exception as error:
        messagebox.showerror("AudioStreamer Device Manager", str(error), parent=root)
        root.destroy()
        return
    root.mainloop()

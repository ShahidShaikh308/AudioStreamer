"""Small Tk desktop controller for the capture and UDP sender."""

from __future__ import annotations

import logging
import queue
import threading
import tkinter as tk
from tkinter import ttk

from server.config import ServerConfig
from server.main import run


class AudioStreamerWindow:
    """Keep controls on Tk's thread and audio/network work on a worker thread."""

    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("AudioStreamer v0.4")
        self.root.minsize(430, 330)

        self._events: queue.Queue[tuple[str, object]] = queue.Queue()
        self._stop_event: threading.Event | None = None
        self._worker: threading.Thread | None = None
        self._closing = False
        self._last_error: str | None = None

        try:
            config = ServerConfig.load()
        except Exception as error:
            config = ServerConfig()
            self._last_error = f"Configuration error: {error}"

        self.receiver_ip = tk.StringVar(value=config.receiver_ip)
        self.receiver_port = tk.StringVar(value=str(config.receiver_port))
        self.protocol_version = tk.StringVar(value=str(config.protocol_version))
        self.streaming_status = tk.StringVar(value="Stopped")
        self.connection_status = tk.StringVar(value="Not streaming")
        self.sample_format = tk.StringVar(value="Not capturing")
        self.packets_sent = tk.StringVar(value="0")
        self.error_text = tk.StringVar(value=self._last_error or "None")

        self._build_widgets()
        self.root.protocol("WM_DELETE_WINDOW", self._close_window)
        self.root.after(100, self._process_events)

    def _build_widgets(self) -> None:
        body = ttk.Frame(self.root, padding=18)
        body.grid(row=0, column=0, sticky="nsew")
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        body.columnconfigure(1, weight=1)

        ttk.Label(body, text="AudioStreamer", font=("Segoe UI", 16, "bold")).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 12)
        )

        ttk.Label(body, text="Receiver IP").grid(row=1, column=0, sticky="w", pady=4)
        self.ip_entry = ttk.Entry(body, textvariable=self.receiver_ip)
        self.ip_entry.grid(row=1, column=1, sticky="ew", pady=4)

        ttk.Label(body, text="UDP Port").grid(row=2, column=0, sticky="w", pady=4)
        self.port_entry = ttk.Entry(body, textvariable=self.receiver_port, width=8)
        self.port_entry.grid(row=2, column=1, sticky="w", pady=4)
        ttk.Label(
            body,
            text="Bundled Android app listens on 5005",
            foreground="#666666",
        ).grid(row=3, column=1, sticky="w", pady=(0, 5))

        ttk.Label(body, text="Protocol").grid(row=4, column=0, sticky="w", pady=4)
        self.protocol_box = ttk.Combobox(
            body,
            textvariable=self.protocol_version,
            values=("2", "1"),
            state="readonly",
            width=8,
        )
        self.protocol_box.grid(row=4, column=1, sticky="w", pady=4)

        controls = ttk.Frame(body)
        controls.grid(row=5, column=0, columnspan=2, sticky="ew", pady=(10, 12))
        self.start_button = ttk.Button(
            controls, text="Start Streaming", command=self._start_streaming
        )
        self.start_button.pack(side="left")
        self.stop_button = ttk.Button(
            controls,
            text="Stop",
            command=self._stop_streaming,
            state="disabled",
        )
        self.stop_button.pack(side="left", padx=(8, 0))

        diagnostics = (
            ("Streaming Status", self.streaming_status),
            ("Connection Status", self.connection_status),
            ("Capture Format", self.sample_format),
            ("Packets Sent", self.packets_sent),
            ("Errors", self.error_text),
        )
        for row, (label, value) in enumerate(diagnostics, start=6):
            ttk.Label(body, text=label).grid(row=row, column=0, sticky="nw", pady=3)
            ttk.Label(body, textvariable=value, wraplength=270).grid(
                row=row, column=1, sticky="w", pady=3
            )

        ttk.Label(
            body,
            text="UDP has no receiver handshake; packet delivery cannot be confirmed here.",
            foreground="#666666",
            wraplength=390,
        ).grid(row=11, column=0, columnspan=2, sticky="w", pady=(12, 0))

    def _start_streaming(self) -> None:
        if self._worker is not None:
            return
        try:
            config = ServerConfig.load()
            config = ServerConfig(
                receiver_ip=self.receiver_ip.get().strip(),
                receiver_port=int(self.receiver_port.get().strip()),
                protocol_version=int(self.protocol_version.get()),
                capture_queue_chunks=config.capture_queue_chunks,
                frames_per_buffer=config.frames_per_buffer,
                log_level=config.log_level,
            )
            config.validate()
            config.save()
        except (OSError, ValueError, TypeError) as error:
            self._show_error(str(error))
            return

        self._last_error = None
        self.error_text.set("None")
        self.streaming_status.set("Starting…")
        self.connection_status.set("Waiting for capture")
        self.sample_format.set("Starting capture…")
        self.packets_sent.set("0")
        self.start_button.configure(state="disabled")
        self.stop_button.configure(state="normal")
        self.ip_entry.configure(state="disabled")
        self.port_entry.configure(state="disabled")
        self.protocol_box.configure(state="disabled")
        self._stop_event = threading.Event()
        self._worker = threading.Thread(
            target=self._stream_worker,
            args=(config, self._stop_event),
            name="AudioStreamer",
            daemon=True,
        )
        self._worker.start()

    def _stream_worker(
        self, config: ServerConfig, stop_event: threading.Event
    ) -> None:
        try:
            run(
                config,
                stop_event=stop_event,
                on_started=lambda device, rate, channels: self._events.put(
                    ("started", (device, rate, channels))
                ),
                on_stats=lambda sent, dropped: self._events.put(
                    ("stats", (sent, dropped))
                ),
            )
        except Exception as error:
            logging.getLogger(__name__).exception("Streaming worker failed")
            self._events.put(("error", str(error)))
        finally:
            self._events.put(("stopped", None))

    def _stop_streaming(self) -> None:
        if self._stop_event is None:
            return
        self.streaming_status.set("Stopping…")
        self.connection_status.set("Stopping")
        self.stop_button.configure(state="disabled")
        self._stop_event.set()

    def _process_events(self) -> None:
        try:
            while True:
                event, payload = self._events.get_nowait()
                if event == "started":
                    device, rate, channels = payload
                    self.streaming_status.set("Streaming")
                    self.connection_status.set("Sending UDP; receiver unconfirmed")
                    self.sample_format.set(f"{device} — {rate} Hz, {channels} channels")
                elif event == "stats":
                    sent, dropped = payload
                    self.packets_sent.set(str(sent))
                    if dropped:
                        self.connection_status.set(
                            f"Sending UDP; {dropped} capture chunks dropped; receiver unconfirmed"
                        )
                elif event == "error":
                    self._last_error = str(payload)
                    self._show_error(self._last_error)
                elif event == "stopped":
                    self._worker = None
                    self._stop_event = None
                    self.streaming_status.set("Error" if self._last_error else "Stopped")
                    self.connection_status.set("Not streaming")
                    self.sample_format.set("Not capturing")
                    self.start_button.configure(state="normal")
                    self.stop_button.configure(state="disabled")
                    self.ip_entry.configure(state="normal")
                    self.port_entry.configure(state="normal")
                    self.protocol_box.configure(state="readonly")
                    if self._closing:
                        self.root.destroy()
                        return
        except queue.Empty:
            pass

        if not self._closing:
            self.root.after(100, self._process_events)

    def _show_error(self, message: str) -> None:
        self.error_text.set(message or "Unknown error")
        self.streaming_status.set("Error")

    def _close_window(self) -> None:
        self._closing = True
        if self._worker is None:
            self.root.destroy()
        else:
            self._stop_streaming()


def launch_gui() -> None:
    root = tk.Tk()
    AudioStreamerWindow(root)
    root.mainloop()

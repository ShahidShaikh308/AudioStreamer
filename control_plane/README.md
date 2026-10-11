# Control Plane

The Control Plane handles identity announcement, discovery, heartbeats, peer expiry, and existing stream lifecycle requests. It is independent of the Audio Plane and never receives PCM. The v1 wire format and validation rules are normative in [`CONTROL_PROTOCOL.md`](../CONTROL_PROTOCOL.md).

Python `protocol/` owns wire types and encoding/parsing. `registry.py` owns peer snapshots and timeout removal. `manager.py` handles discovery and command/status policy. On Windows, `broker.py` owns the single UDP socket and relays datagrams through an authenticated named pipe; `transport.py` is the per-process pipe client. The Windows node and Device Manager reuse this implementation.

Android mirrors v1 in `platforms/android/app/src/main/java/com/audiostreamer/control_plane/`: `ControlProtocol.kt` contains typed packets and codec, `NodeRegistry.kt` owns peer snapshots, and `ControlManager.kt` owns the UDP socket in a private IO coroutine scope with bounded outbound messages.

ESP32 has a separate C++ v1 implementation under `esp32_controller/`. Its control worker owns networking, while the UI owns presentation and input. All implementations use the same message IDs and field layout.

Control commands are unacknowledged UDP requests. A successful local send does not establish that a peer accepted a command or that audio is audible. IPv4 broadcast may be blocked by firewall, VPN, or Wi-Fi client isolation; validate discovery and stream commands on physical devices.

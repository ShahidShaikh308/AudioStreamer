# Control Plane Boundary

The Control Plane is a planned, independent orchestration system. This directory intentionally contains no runtime implementation.

Its future responsibilities are device discovery, registration, heartbeat, routing, device selection, and connection management. It may exchange commands and status such as `CONNECT`, `STOP`, `DISCONNECT`, and `STATUS` with node applications.

The Control Plane must never transport PCM audio. It must not become a dependency of protocol parsing, packetization, UDP audio transport, buffering, or playback. Audio nodes must remain operable with statically configured endpoints when no Control Plane is present.

Before implementing this plane, define the command/status interface and lifecycle ownership separately from the audio wire protocol. Control messages must not alter protocol v3 datagrams.

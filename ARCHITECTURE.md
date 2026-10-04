# AudioStreamer Architecture

## Design model

AudioStreamer is a distributed audio transport platform. A participating device is a **node** with a set of capabilities: capture, send, receive, and/or play. Those capabilities may be assigned runtime roles; the architecture does not make any device a permanent server or client.

The system has two independent planes:

### Audio Plane

The Audio Plane transports PCM and performs no orchestration. It includes capture adapters, shared PCM models, the wire protocol, packetization, UDP transport, receive parsing, buffering, and playback. It must not import Control Plane, UI, device discovery, or routing code.

Current path:

```text
Windows Audio Engine
  → WASAPI loopback adapter
  → PcmFormat / PcmChunk
  → protocol v3 serialization and frame-aligned packetization
  → UDP sender

UDP receiver → protocol v3 parser → bounded packet queue → platform playback
                                                        ├─ Windows PortAudio
                                                        └─ Android AudioTrack
```

Python protocol serialization/deserialization lives in `audio_plane/protocol/`. `audio_plane/transport/udp_sender.py` accepts PCM model values and sends serialized datagrams; `udp_receiver.py` validates incoming datagrams and emits typed packets through a callback. Neither transport owns application lifecycle or device APIs. `audio_plane/buffering/packet_queue.py` bounds playout delay. Windows capture and playback adapters own their respective device APIs. Android has corresponding protocol, transport, buffering, and playback boundaries.

### Control Plane

The Control Plane is responsible for orchestration: discovery, registration, heartbeat, routing, destination selection, and connection management. It may issue commands such as `CONNECT`, `STOP`, `DISCONNECT`, and `STATUS`; it never transports PCM payloads.

No Control Plane runtime is implemented yet. The `control_plane/` directory documents the reserved boundary only. Today, a Windows user statically enters one peer destination IPv4 address/port and a local receive port in the desktop UI/configuration. That is application configuration, not discovery or dynamic routing.

When implemented, control commands must cross the boundary through a small command/status interface. Audio modules must remain functional without a Control Plane process. Control code may request audio lifecycle changes, but must not inspect, packetize, or relay PCM.

## Repository map

```text
audio_plane/
  models.py                    Shared PCM format/chunk value types
  buffering/                   Duration-bounded packet playout queue
  protocol/                    Active protocol v3 constants, packet, parser, serializer
  transport/                   UDP sender and protocol-validating receiver
core/
  audio/interfaces.py          Platform-neutral audio component contracts
  node/                        INode, capabilities, typed events, lifecycle state
control_plane/
  README.md                    Planned scope and dependency boundary
platforms/
  windows/
    capture/                   WASAPI loopback adapter
    playback/                  Default Windows output through PortAudio
    desktop/                   Configuration, logging, Tk UI, WindowsNode/composition root
    requirements.txt            Windows Python runtime dependency
  android/
    app/src/main/java/.../
      audio_plane/              Android protocol, UDP, buffer, playback modules
      platform/android/          Android application UI and lifecycle
config/                         User-editable Windows settings
PROTOCOL.md                     Normative v3 packet specification
```

## Dependency direction

Platform adapters depend on Audio Plane abstractions. Protocol and transport do not depend on a platform capture API or user interface. The Windows node composes capture plus sender and independently composes UDP receiver, queue, and playback. The Android application composes UDP receive, parsing, queueing, and playback.

The intended dependency shape is:

```text
Platform application → platform adapters → Audio Plane contracts
Control Plane         → command/status boundary → platform application
Audio Plane           ✕ no dependency on Control Plane
```

The platform-neutral node API is defined under `core/node/`; audio engine contracts are in `core/audio/interfaces.py`. The Windows composition root injects the existing capture, transport, queue, and playback implementations into `WindowsAudioNode`. The desktop UI depends only on `INode`, settings values, and typed node events. See [docs/NODE_ARCHITECTURE.md](docs/NODE_ARCHITECTURE.md) for event delivery, lifecycle states, and extension rules.

## Current runtime characteristics

- Windows capture uses the selected default WASAPI loopback endpoint's mix rate and channel layout; the active sender requires stereo float32.
- Windows playback uses the default PortAudio output and configures its stream to the incoming packet sample rate.
- Windows sender and receiver run independently and may be active simultaneously.
- UDP carries protocol v3 audio datagrams to a configured destination and receive port.
- Protocol details, limits, and validation are specified in [PROTOCOL.md](PROTOCOL.md).
- Receivers accept stereo IEEE-754 float32 little-endian PCM. Android writes through `AudioTrack`; Windows writes through PortAudio.
- Software queues are bounded by audio duration and a hard packet-count ceiling (Windows target: 80 ms).
- UDP is best effort. There is no acknowledgement, retransmission, clock synchronization, congestion control, encryption, compression, or discovery.

These characteristics describe the current code, not requirements for every future node. New platform capabilities should be isolated behind platform-specific adapters without changing protocol v3 semantics.

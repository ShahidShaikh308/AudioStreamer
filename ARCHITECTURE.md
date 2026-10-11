# AudioStreamer Architecture

AudioStreamer is a distributed LAN audio project. A device is a node whose available capabilities determine whether it can capture, send, receive, or play audio. The system does not assign permanent server/client roles.

## Independent planes

### Audio Plane

The Audio Plane transports PCM and performs no orchestration. It contains capture adapters, the protocol v3 packet model/serializer/parser, UDP transport, bounded buffering, and platform playback.

```text
Windows WASAPI capture ─┐                       ┌─ Windows UDP receive → bounded queue → PortAudio
                        ├→ protocol v3 → UDP ───┤
Android playback capture┘                       └─ Android UDP receive → bounded queue → AudioTrack
```

Shared Python PCM/network components are under `audio_plane/`. Platform implementations are isolated under `platforms/windows/` and `platforms/android/`. The protocol contract is [PROTOCOL.md](PROTOCOL.md). Audio UDP is best effort and currently has no compression, encryption, retransmission, or clock synchronization.

### Control Plane

The Control Plane handles discovery, registration, heartbeat, node status, and existing START/STOP stream requests. It never carries PCM. Its independent v1 wire contract is [CONTROL_PROTOCOL.md](CONTROL_PROTOCOL.md).

On Windows, `control_plane/broker.py` is the sole owner of the configured UDP Control Plane socket. Local Windows node and Device Manager clients attach through an authenticated named pipe; each retains its own node identity and registry. The broker binds without address reuse. `control_plane/manager.py` owns per-client control policy, `registry.py` owns peer snapshots, `protocol/` owns the Python v1 representation, and `transport.py` connects the clients to the broker.

Android implements the same v1 messages in `platforms/android/app/src/main/java/com/audiostreamer/control_plane/`. One `Dispatchers.IO` coroutine owns its UDP socket and performs all control send/receive work. A bounded channel receives outbound messages from the node/UI boundary. The Kotlin codec and registry have no Activity dependency.

The ESP32 is a control-only hardware client. Its FreeRTOS worker owns UDP discovery, heartbeats, peer maintenance, and queued stream commands. The OLED/joystick UI does not transport audio and does not own network operations.

## Node and UI boundaries

`core/audio/interfaces.py` defines platform-neutral audio component contracts. `core/node/` defines the Windows node lifecycle, settings, identity, immutable snapshots, and typed events. The Windows composition root injects WASAPI capture, protocol v3 UDP send/receive, a duration-bounded queue, and PortAudio playback into `WindowsAudioNode`. Sender and receiver lanes run independently.

Android has a native Kotlin node contract. `AndroidNode` owns settings and lane lifecycles, while `MainActivity` observes a `StateFlow` and issues node operations. Android mixed playback capture requires API 29+, user consent, and eligible source-app capture policy. The capture callback's reusable PCM memory is copied into a bounded reusable frame pool before asynchronous network transmission.

The Windows Device Manager is a control-only desktop UI. The ESP32 is a hardware control-only client. Both use the same existing Control Protocol semantics, but neither owns or implements Audio Plane functionality.

## Repository layout

```text
audio_plane/                  Shared PCM protocol v3, UDP, and buffering
control_plane/                Python Control Protocol v1 and Windows broker
control_client/               Standalone Windows Device Manager
core/                         Platform-neutral Python component/node contracts
platforms/windows/            WASAPI capture, playback, node composition, desktop UI
platforms/android/            Kotlin node, audio/control protocols, Android adapters/UI
esp32_controller/              Single active ESP32 PlatformIO project
config/                        Safe example; local runtime overrides are ignored
docs/                          Supporting architecture notes
PROTOCOL.md                    Normative Audio Protocol v3
CONTROL_PROTOCOL.md            Normative Control Protocol v1
```

See [PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md) for folder detail and [BUILDING.md](BUILDING.md) for commands.

## Constraints and validation

- Audio and Control protocols are independently versioned.
- Control commands are best-effort requests, not proof that playback started.
- ESP32 has no audio capability bits and no PCM code.
- A compile confirms source/build consistency only. LAN discovery, Android permission behavior, hardware input, real-time scheduling, and audible output require physical-device validation.
- Control Protocol v1 is IPv4 and unauthenticated on the LAN. Use a trusted network.

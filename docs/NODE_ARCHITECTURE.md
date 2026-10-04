# AudioStreamer Node Framework

## Purpose

The node framework provides a platform-neutral lifecycle and status surface around the existing Audio Plane. A node is a capability-bearing process; sender and receiver capabilities have independent lifecycles and may run at the same time. The framework does not change protocol v3, packetization, capture behavior, buffering policy, or playback behavior.

## Responsibilities

`core/node/` defines the Windows-side `INode`, immutable settings and identity values, capability flags, typed events, snapshots, and permitted lifecycle transitions. Android mirrors this contract with Kotlin `INode`, `NodeSettings`, `NodeSnapshot`, and independent lane states under `platforms/android/.../core/node/`, because the Python package cannot be linked into a native Kotlin application.

`core/audio/interfaces.py` defines structural contracts for capture, sender, receiver, queue, and playback implementations. These contracts carry existing PCM and packet types; they do not own implementations or change their behavior.

`platforms/windows/desktop/bootstrap.py` is the Windows composition root. It injects WASAPI capture, the existing v3 UDP sender/receiver, the existing bounded packet queue, and PortAudio playback into `WindowsAudioNode`. The node owns the created engines for each active lane and releases them when workers end.

`platforms/windows/desktop/node.py` implements `INode`, coordinates independent workers, maps engine status into node states, and publishes events. It does not define packet layouts or transport serialization.

`platforms/windows/desktop/gui.py` accepts only `INode`. It sends settings and lifecycle requests through the contract and subscribes to immutable events. Event callbacks run on worker threads and only enqueue data; Tk drains that queue on its event loop before touching widgets. The short Tk timer drains queued events only; it does not poll sockets, threads, or node internals.

## Dependency shape

```mermaid
flowchart LR
    UI[Desktop UI] -->|INode requests and subscriptions| NW[WindowsAudioNode]
    BOOT[Windows composition root] -->|injects factories| NW
    NW -->|IAudioCapture| CAP[WASAPI capture]
    NW -->|IAudioSender| SEND[UDP sender]
    NW -->|IAudioReceiver| RECV[UDP receiver]
    NW -->|IAudioQueue and IAudioPlayback| QUEUE[Bounded packet queue]
    QUEUE --> PLAY[PortAudio playback]
    SEND --> PROTO[Protocol v3]
    RECV --> PROTO
```

The Control Plane may later use the same node contract to issue lifecycle commands and observe status. It remains outside the Audio Plane and has no access to PCM payloads through this interface.

## Lifecycle model

Sender and receiver each have `STOPPED`, `STARTING`, `RUNNING`, `STOPPING`, and `ERROR` states. A node snapshot reports both lane states and an aggregate status. A healthy running lane keeps aggregate status `RUNNING` while the other lane starts or stops; a lane error makes the aggregate `ERROR` while preserving the other lane's independent state.

Node listeners receive typed events for state changes, format and statistics updates, errors, and completed shutdown. Listeners must be quick and thread-safe. A UI listener should marshal events to its UI thread instead of mutating UI controls from a worker callback.

`shutdown()` requests both lanes to stop; it does not block the caller. `SHUTDOWN_COMPLETE` is published after sender, receiver, and playback workers have returned and released their resources. A lane can be stopped or restarted without stopping the other lane.

## Extension guidance

- Implement `INode` for another platform; keep its device APIs in `platforms/<platform>/`.
- Inject components through a platform composition root, not through GUI or protocol modules.
- Keep Audio Plane component interfaces focused on operation and lifecycle. Do not add UI state or discovery to them.
- Keep settings platform-neutral at the node boundary; map platform-only details inside the implementation.
- Do not interpret a UDP send count as delivery confirmation. The current protocol has no acknowledgements.

## Current limits

Windows and Android now implement equivalent platform-native node contracts. Discovery, registration, heartbeat, dynamic routing, and Control Plane command delivery are not implemented. Android transport, capture, acoustic behavior, and long-duration stability still require runtime validation on physical devices.

## Android node

`AndroidNode` implements the equivalent Kotlin node contract and owns settings, state, sender, receiver, `AudioPlayer`, and lifecycle coordination. `MainActivity` observes the node's `StateFlow` and issues node operations; it does not own sockets, queues, capture jobs, or playback engines. Android settings are saved in app-private `SharedPreferences`.

`AndroidPlaybackCapture` is the platform capture adapter. It requests `RECORD_AUDIO` and the system MediaProjection consent, then starts `PlaybackCaptureService` as a media-projection foreground service. The service captures 48 kHz stereo float PCM into fixed reusable buffers, excludes AudioStreamer’s own UID to prevent receive-to-send feedback, and invokes the node's synchronous PCM callback. The Android v3 sender reuses a datagram buffer and the Kotlin serializer that mirrors the frozen root `PROTOCOL.md` layout. The Kotlin receiver, parser, bounded queue, and AudioTrack player remain the existing receive path.

Android playback capture exists from Android 10 (API 29). It captures only eligible `USAGE_MEDIA`, `USAGE_GAME`, or `USAGE_UNKNOWN` playback from apps whose capture policy permits it, with user-approved MediaProjection. Audio from protected or opted-out apps cannot be captured. The app requests `RECORD_AUDIO`; it does not use the microphone as an audio source. Android 14/API 34+ requires the media-projection foreground-service declaration and permission. Because the app targets API 37, local UDP send and receive also require the `ACCESS_LOCAL_NETWORK` runtime permission. The receive/playback-only path can run on older supported Android versions; mixed playback sending reports unsupported below API 29.

Android end-to-end transport and physical audio quality require Android devices. A successful Gradle build or serializer inspection does not prove Android-to-Windows, Android-to-Android, or acoustic playback behavior.

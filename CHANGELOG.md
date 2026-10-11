# Changelog

This changelog records user-visible project milestones and architecture changes. It is not a claim that every milestone has a signed or published binary release.

## AudioStreamer v1 Phase 7 — ESP32 Control Plane integration

- Added the ESP32 Control Plane client with persistent identity, discovery, heartbeat/peer maintenance, and existing v1 stream command support.
- Added joystick-driven Main Menu, Sender Selection, Receiver Selection, Confirm, Streaming, and Settings screens with dynamic peers.
- Updated repository onboarding and build documentation for Windows, Android, and ESP32. No Audio Protocol or Control Protocol wire behavior was changed by this documentation milestone.

## AudioStreamer v1 Phase 3 — Android node

- Added Android playback capture, an independent protocol v3 UDP sender, and the `AndroidNode` lifecycle while retaining the existing receive/playback path.

## AudioStreamer v1 Phase 1 — Windows node

- Established the Windows reference node with independent WASAPI send and UDP receive/playback capabilities.

## AudioStreamer v1 Phase 6 — Control Plane stabilization

- Changed the default configurable Control Plane UDP port to 55050; Audio Protocol v3 and Audio Plane ports remain unchanged.
- Added a Windows Control Broker as the sole UDP socket owner, with an authenticated local named-pipe bus shared by the Windows node, Device Manager, and future local tools.
- Moved Android Control Plane socket creation, binding, send, receive, heartbeat, and shutdown to a private `Dispatchers.IO` coroutine scope with a bounded outbound channel.
- Moved Android sender socket construction and address resolution off the main thread; moved local-IP enumeration off the main thread.
- Added Windows startup diagnostics for broker bind/startup failures and documented the Control Plane transport ownership model.

## AudioStreamer v1 Phase 5 — Desktop Device Manager

- Added a standalone Tk Control Plane client that reuses the existing Python ControlManager and keeps audio-node composition separate.
- Restored a compact Tk Device Manager with a live peer table and one sender/receiver selection pair for existing START/STOP requests.
- Added an optional append-only 16-bit transaction ID to Control Protocol v1 stream commands, status, and errors; legacy prefixes and Audio Protocol v3 remain unchanged.
- Added controller status return routing to Python and Android node managers and documented legacy correlation and single-destination audio limitations.
- Added persisted Device Manager settings and a dedicated Windows launcher.

## AudioStreamer v1 Phase 2.5 — Node framework

- Added platform-neutral `INode`, capability, settings, identity, snapshot, event, and lifecycle contracts.
- Added structural audio component interfaces for capture, sender, receiver, bounded queues, and playback.
- Refactored the Windows node to receive component factories from a Windows composition root; sender and receiver lifecycles remain independent.
- Changed the desktop UI to use only `INode` operations and subscribed events, marshalled through Tk's event queue.
- Removed the duplicate Windows sender orchestration module; protocol v3, capture, buffering, playback, and Android implementations remain unchanged.

## AudioStreamer v1 Windows node (in progress)

- Added a protocol-validating Windows UDP receiver and bounded audio-duration playback queue.
- Added default Windows output playback through PyAudioWPatch/PortAudio.
- Added independent sender and receiver lifecycle controls to the Windows desktop node.
- Extended persistent Windows settings with a node name, receive port, and future-compatible audio-device index.
- Kept the sender on the protocol v3 serializer and UDP sender; the wire format is unchanged.
- Kept shared protocol/transport under `audio_plane/`, platform implementations under `platforms/`, and the normative protocol at the repository root.
- Preserved Android's established application ID and documented the Control Plane boundary without implementing it.

## AudioStreamer v0.5

- Established protocol v3 as the active compact UDP PCM format and retired v1/v2 from active sender/receiver code.
- Added stream restart identification and sequence tracking to the v3 packet header.
- Bounded Android software playback buffering by queued audio duration rather than a fixed packet count.
- The v3 sender/receiver must be upgraded together; v3 datagrams are not wire-compatible with v2.

## AudioStreamer v0.4 prototype

- Reached a working Windows WASAPI loopback → UDP PCM → Android `AudioTrack` playback path.
- Added a desktop launcher/interface and basic receiver diagnostics for evaluation.
- This milestone was an end-to-end prototype, not a claim of production network guarantees.

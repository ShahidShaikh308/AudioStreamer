# Changelog

This changelog records user-visible project milestones and architecture changes. It is not a claim that every milestone has a signed or published binary release.

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

# Control Protocol v1

This document defines the current Control Plane datagram formats used by the Windows Service, Android node, and ESP32 controller. It is separate from [Audio Protocol v3](PROTOCOL.md): Control Protocol packets never carry PCM.

## Common header

All multi-byte integers are unsigned big-endian. A UDP datagram is exactly one complete Control Protocol packet.

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 2 | Magic | ASCII `AC` (`0x41 0x43`) |
| 2 | 1 | Version | `1` |
| 3 | 1 | Message type | Type table below |
| 4 | 2 | Payload length | Payload bytes following the 6-byte header |
| 6 | variable | Payload | Message-specific fields |

The maximum datagram is 1200 bytes. The payload length must exactly equal the datagram length minus six.

## Message types

| Value | Type | Payload fields, in order |
|---:|---|---|
| 1 | `HELLO` | Node ID (u64), name (u8 byte length + UTF-8), platform (u8 byte length + UTF-8), capability mask (u16), Audio Plane receive port (u16) |
| 2 | `GOODBYE` | Node ID (u64) |
| 3 | `HEARTBEAT` | Node ID (u64), node status (u8) |
| 4 | `START_STREAM` | Sender Node ID (u64), receiver Node ID (u64), optional transaction ID (u16) |
| 5 | `STOP_STREAM` | Sender Node ID (u64), receiver Node ID (u64), optional transaction ID (u16) |
| 6 | `STREAM_STATUS` | Sender Node ID (u64), receiver Node ID (u64), state (u8), error code (u16), optional transaction ID (u16) |
| 7 | `ERROR` | Reporting Node ID (u64), error code (u16), text (u8 byte length + UTF-8), optional transaction ID (u16) |

The receive port in `HELLO` is the node's **audio** receive port. Control unicast packets are sent to the configured shared Control Plane UDP port (default `55050`); the source UDP endpoint is used for peer replies and routing where the implementation supports it.

Node IDs are nonzero u64 values. Strings are UTF-8, limited to 255 encoded bytes, and may not contain NUL. Names and platforms in HELLO must not be empty. The ERROR message text may be empty.

## Enumerated fields

Capability mask bits: `CAPTURE=0x0001`, `PLAYBACK=0x0002`, `SENDER=0x0004`, `RECEIVER=0x0008`; all other bits are reserved and must be zero.

Node status values: `OFFLINE=0`, `ONLINE=1`, `BUSY=2`, `STREAMING=3`, `ERROR=4`. OFFLINE is a local state and is not sent in HEARTBEAT.

Stream state values: `RUNNING=1`, `STOPPED=2`, `FAILED=3`.

## Validation and extensions

Receivers reject unknown magic, versions, message types, invalid enum values, reserved capability bits, malformed UTF-8, truncated required fields, inconsistent payload lengths, and datagrams over 1200 bytes. Node IDs in identity and stream-command messages must be nonzero; START/STOP endpoints must differ. Optional transaction IDs, when present, are read from the first two trailing bytes; one trailing byte is malformed.

The established v1 parsers accept extra trailing bytes after a known message prefix (and ignore bytes following a recognized transaction ID). Do not assign meaning to such bytes within v1 without coordinating all implementations. A future incompatible required layout needs a new version. New message types or capability bits require coordinated implementation; unknown types/bits are rejected by current clients. Audio Protocol versioning is independent.

## Authenticated controller commands (v2)

Discovery and peer status remain unauthenticated Control Protocol v1 messages. Controller-issued `START_STREAM` and `STOP_STREAM` use a separate v2 envelope; v1 node-to-node commands remain supported only when the datagram source address matches the advertised peer endpoint. A controller does not send `HELLO`, `HEARTBEAT`, or `GOODBYE`, and therefore is not inserted into audio-node discovery.

The v2 packet uses the common six-byte header with version `2` and message type `4` (`START_STREAM`) or `5` (`STOP_STREAM`). Its payload is exactly:

| Field | Size | Meaning |
|---|---:|---|
| Controller Node ID | 8 | Nonzero stable controller identity |
| Sender Node ID | 8 | Audio stream sender |
| Receiver Node ID | 8 | Audio stream receiver; must differ from sender |
| Transaction ID | 2 | Request correlation ID |
| Nonce | 16 | Cryptographically random, nonzero per-command value |
| HMAC-SHA256 | 32 | MAC of the complete 48-byte header and payload prefix |

All integers use network byte order. Total datagram size is 80 bytes. The HMAC is verified in constant time. Receivers reject a missing key, malformed packet, invalid MAC, and a nonce already seen from that controller. Implementations keep a bounded 1,024-entry replay window for ten minutes. `STREAM_STATUS` responses remain v1 and are informational; they do not authorize actions.

The shared secret is 32 random bytes represented as 64 hexadecimal characters. Use the same value on every Windows and Android node and the ESP32 controller. For example, generate one on Windows with:

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

On Windows, set `ControlPlane:ControllerSecret` in `%ProgramData%\AudioStreamer\runtime-settings.json` and restart the AudioStreamer service. On Android, enter the value in **Node configuration → Controller shared secret** and save; Android encrypts the value with an Android Keystore key. For ESP32, define `AUDIOSTREAMER_CONTROLLER_SECRET_HEX` in the private `include/audiostreamer/config/WifiSettings.h` before building and flashing. The private header is ignored by Git. Never commit or log the key. An empty/missing key disables controller commands while preserving v1 peer-to-peer operation.

Older receivers reject v2 commands, while v1 discovery and node-to-node streaming remain wire-compatible. Controller support therefore requires updating all participating receivers before enabling commands. The controller remains absent from the peer list.

## Transport and security

The Control Plane uses IPv4 UDP broadcast for discovery and heartbeats and unicast for node commands/status. On Windows, the Service hosts the Engine and its `WindowsControlTransport` adapter is the sole local owner of the configured Control Plane UDP socket. IPC carries node commands, snapshots, and events; it does not relay raw Control datagrams. Android and ESP32 own their respective Control Plane sockets. UDP is best effort; neither version encrypts traffic or adds retransmission. V1 is unauthenticated and intended for a trusted LAN; v2 authenticates controller stream commands with the shared key.

## .NET implementation status

`src/AudioStreamer.Control.Protocol/ControlProtocolV1Codec.cs` and `AuthenticatedControllerCommandCodec.cs` are the authoritative .NET serializers/parsers for their respective versions. The Engine `ControlCoordinator` owns discovery, heartbeat/expiry policy, endpoint command routing, authenticated controller command verification, transaction status, replay rejection, and recovery decisions. `src/AudioStreamer.Platform.Windows/WindowsControlTransport.cs` owns the Windows UDP socket, send/receive loops, and network-change notifications; it transfers opaque datagram bytes through `IControlTransport` and does not interpret protocol messages. The Engine `PeerManager` owns authoritative peer records and discovered endpoints. C# tests compare protocol behavior and message layouts; LAN interoperability and long-duration recovery still require hardware validation.

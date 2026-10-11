# Control Protocol v1

This document defines the current Control Plane datagram format used by the Python Windows node/Device Manager, Android node, and ESP32 controller. It is separate from [Audio Protocol v3](PROTOCOL.md): Control Protocol packets never carry PCM.

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

## Transport and security

The Control Plane uses IPv4 UDP broadcast for discovery and heartbeats and unicast for node commands/status. The Windows broker is the sole local owner of its configured Control Plane UDP socket and relays datagrams to same-host applications through a named pipe. Android and ESP32 own their respective Control Plane sockets. UDP is best effort; v1 has no acknowledgements, authentication, encryption, or retransmission. Use only on a trusted LAN.

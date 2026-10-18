# AudioStreamer Local IPC Protocol

**Status:** Frozen v1 local IPC specification for AudioStreamer v2.
**Transport:** Local Windows Named Pipe.
**Independent protocols:** This is not Audio Protocol v3 or Control Protocol v1. See [protocol documents](PROTOCOL.md) and [Control Protocol](CONTROL_PROTOCOL.md).

## 1. Scope and invariants

IPC carries UI commands, responses, immutable state snapshots, events, and bounded diagnostics between the WinUI process and Windows Service. It never carries PCM or forwards arbitrary LAN datagrams. Only the service owns Windows node sockets, audio devices, discovery, and registry.

The Windows Service is the single source of truth for node state, settings, peers, diagnostics, and lifecycle. The UI is a client. Closing, quitting, crashing, or restarting a UI connection must not stop or pause Engine work, discovery, Control Plane communication, or audio lanes. IPC request acceptance is not evidence that a UDP message reached a peer or that audio is audible.

The current .NET Service supports `Ping`, `GetSnapshot`, `GetStatus`, `GetSettings`, `UpdateSettings`, `GetVersion`, `GetDiagnostics`, `GetStreams`, `GetPeers`, `GetPeer`, `GetControlStatistics`, `StartStream`, `StopStream`, `Pause`, `Resume`, `RestartDiscovery`, `Subscribe`, and `Unsubscribe`, plus `Hello` and `Heartbeat`. Other commands remain reserved and return `CapabilityUnavailable`. `Pause` and `Resume` accept `{ "sender": bool, "receiver": bool }` and apply to active local lanes. `Ping` is read-only; IPC `Heartbeat` is connection liveness and has no node-state effect.

## 2. Transport and framing

- Pipe name: `\\.\pipe\AudioStreamer.Node.v1` (implementation constant; not a protocol wire port).
- Local named-pipe transport only; remote pipe clients are disabled.
- Each frame: 4-byte unsigned little-endian payload length, then exactly that many UTF-8 JSON bytes, no BOM.
- Maximum frame: **1,048,576 bytes (1 MiB)**. Minimum accepted body: 2 bytes. Reject length 0, lengths above maximum, incomplete frames, malformed UTF-8/JSON, and trailing bytes outside the framed body.
- Pipe byte mode with explicit framing; one writer task per connection serializes responses and events to avoid interleaving.
- Maximum concurrent UI clients: 8. Each connection has bounded request and outbound-event queues. A slow client is sent `ResyncRequired` when possible, then disconnected if it remains behind.
- No PCM/audio payloads, opaque binary blobs, paths to executables, or arbitrary command names are permitted in payloads.

## 3. JSON envelope and schema rules

UTF-8 JSON objects use camelCase names. Numeric values are bounded and validated before use. Unknown optional payload fields are ignored; unknown envelope versions or required fields fail with a typed error. Additive optional fields are preferred. A field’s meaning or type is never changed within the same major schema version. Envelopes carry an ISO 8601 UTC `timestamp` for display; deadlines use monotonic timers.

Request:

```json
{
  "schemaVersion": {"major": 1, "minor": 0},
  "kind": "request",
  "messageType": "GetSnapshot",
  "requestId": "b3ed4175-5748-4b17-9cef-03c807932130",
  "connectionEpoch": "e0d429c4-a8f9-4d79-a0c6-98f7eab89540",
  "payload": {}
}
```

Response:

```json
{
  "schemaVersion": {"major": 1, "minor": 0},
  "kind": "response",
  "messageType": "GetSnapshotResult",
  "requestId": "b3ed4175-5748-4b17-9cef-03c807932130",
  "connectionEpoch": "e0d429c4-a8f9-4d79-a0c6-98f7eab89540",
  "payload": {"result": "ok", "snapshot": {"nodeState": "idle"}}
}
```

Event:

```json
{
  "schemaVersion": {"major": 1, "minor": 0},
  "kind": "event",
  "messageType": "PeerDiscovered",
  "eventSequence": 42,
  "connectionEpoch": "e0d429c4-a8f9-4d79-a0c6-98f7eab89540",
  "payload": {"nodeId": "0000000000000001", "name": "Studio PC"}
}
```

Node IDs are fixed-width 16-character lowercase hex strings to avoid JSON number precision loss. Ports/counters use bounded integers. Timestamps use ISO 8601 UTC for display only; monotonic time is not exported as a cross-process clock.

## 4. Handshake and version negotiation

1. Client connects and sends `Hello` within 5 seconds with supported schema major/minor range, UI version, process ID, and requested role `interactive`.
2. Server validates the Windows client token and authorization, then returns `HelloResult` with selected schema, service version, Engine version, min/max supported UI version, capabilities, connection epoch, service state, and current NodeHost state.
3. Major versions must match. The server selects the highest common minor version; unknown optional fields are ignored. If no common version exists, return `IpcVersionUnsupported` and close gracefully.
4. Client sends `Subscribe` first, then `GetSnapshot`. The server begins bounded event buffering for that connection before taking the snapshot; the snapshot includes an `eventSequence` watermark. Client applies the snapshot, discards queued events with sequence ≤ watermark, then applies later events in sequence order. This prevents a state change between snapshot and subscription from being lost.
5. On reconnect, a new connection epoch is created and the client repeats the handshake and snapshot. Old requests/events are invalid.

Request timeout: 5 seconds for read operations, 15 seconds for ordinary mutations, and at most 30 seconds for lifecycle operations. Long work returns promptly with `accepted` and an `operationId`; completion arrives through `OperationCompleted`/state events. The IPC client sends a `Heartbeat` every 30 seconds; the service replies `HeartbeatAck`. Three missed acknowledgements (90 seconds) close only that client connection. The heartbeat timer is owned by the IPC client and has no authority over Engine/service state. The service does not depend on a connected client to run the node. No high-frequency state polling is required.

## 5. Commands

All commands are allow-listed. Each request returns one correlated response, even when invalid. Payload schemas are strict and command-specific.

| Command | Purpose | Key constraints |
|---|---|---|
| `Ping` | Confirm the Service can answer a local request | Read-only; does not imply Engine or peer readiness |
| `GetSnapshot` | Full node, plane, lane, and service state | Read-only; includes event watermark |
| `GetStatus` | Current Engine/Control Plane/Audio Plane lifecycle and health | Read-only; IPC connection state remains client-local |
| `GetPeers` | Current immutable peer snapshot | Does not refresh discovery |
| `GetPeer` | One discovered peer by Engine-facing GUID | Returns `PeerNotFound` when it is not currently in the registry |
| `GetControlStatistics` | Control datagram, heartbeat, malformed/unknown packet, timeout, reconnect, session, discovery-duration and RTT aggregates | RTT is null until a correlated status sample is available |
| `GetStreams` | Current stream and sender/receiver lane snapshots | Read-only; local state is not remote audible confirmation |
| `GetSettings` | Current canonical Engine `SettingsSnapshot` | Read-only; secret values are never returned |
| `GetStatistics` | Current bounded aggregate counters/gauges | Read-only; includes units and sample time |
| `StartNode` / `StopNode` | Explicitly start or drain NodeHost | Idempotent; does not start/stop Windows service; never used by UI close/Quit/reconnect |
| `StartStream` / `StopStream` | Request a route using existing Control v1 semantics | Nonzero distinct node IDs; capability and peer validation; no guaranteed delivery claim |
| `Pause` / `Resume` | Pause or resume selected active local sender/receiver lanes | At least one selected lane must be active; remote peer is not commanded |
| `Pause` / `Resume` | Apply documented local pause semantics | Explicit target lanes; idempotent |
| `UpdateSettings` | Change validated machine settings | Versioned patch; atomic save; rejected fields returned |
| `GetAudioDevices` | Enumerate current capture/render devices | Stable endpoint IDs; bounded list |
| `SelectAudioDevices` | Select endpoint IDs or explicit follow-default mode | Validate availability; may require lane restart |
| `RestartDiscovery` | Restart Control Plane discovery worker | Rate-limited; no protocol change |
| `GetDiagnostics` | Read bounded Engine/adapter diagnostics and health snapshot | Read-only; process/SCM details come from host health; no aggressive polling |
| `GetLogsPage` | Read a bounded redacted tail/page | Restricted authorization; maximum page size; not a log stream |
| `GetVersions` | Return product, Service, Engine assembly, and IPC schema versions | Read-only |
| `Subscribe` / `Unsubscribe` | Start/end pushed event delivery for this pipe connection | Subscription does not control Engine workers or Engine-owned observers |
| `Heartbeat` | Confirm local IPC connection responsiveness | Request receives `HeartbeatAck`; no node, discovery, Control Plane, or audio state effect |
| `BeginUpdateCheck` | Ask installed updater to check signed metadata | Future capability; no file paths/URLs supplied by UI |

`StopService`, arbitrary shell/exec, raw socket send, and generic filesystem operations are not IPC commands. Service update/repair is performed by installer coordination under elevation.

## 6. Responses and errors

Every response echoes `requestId` and `connectionEpoch`. `payload.result` is `ok`, `accepted`, or `error`. Errors use stable `code`, safe user-facing `message`, optional `fieldErrors`, and `retryable`; never expose stack traces or secrets. Stable error families include:

- `InvalidRequest`, `InvalidField`, `Unauthorized`, `RateLimited`
- `NodeNotRunning`, `NodeStarting`, `NodeStopping`, `CapabilityUnavailable`
- `PeerNotFound`, `PeerCapabilityMismatch`, `InvalidStreamPair`
- `DeviceUnavailable`, `ConfigurationConflict`, `OperationTimedOut`
- `IpcVersionUnsupported`, `ServiceStopping`, `InternalError`

An accepted asynchronous operation includes an `operationId`. The final result is an event; the request is not left hanging for audio/network completion.

## 7. Events and delivery semantics

The Engine publishes `PeerAdded`, `PeerUpdated`, `PeerRemoved`, `SessionStarted`, `SessionStopped`, `ControlStreamStatus`, `ControlError`, `NetworkLost`, and `NetworkRecovered` as domain events; the Service only maps those events to the existing IPC event envelope. Engine events also include `SessionCreated`, `SessionPrepared`, `SessionStarted`, `SessionRecovered`, `SessionStopped`, `SessionFailed`, `SessionChanged`, `SessionsChanged`, `AudioUnderrun`, `AudioOverrun`, `PacketLossDetected`, `PacketRecovered`, audio transport/jitter counters, statistics, configuration, and lifecycle changes. These event names describe local observed/requested states; they do not prove audio is audible. End-to-end latency, clock drift, and per-session capture/playback rates are not yet included in the diagnostics snapshot.

Events are ordered per connection and have monotonically increasing `eventSequence`. Events are not durable across service restarts or disconnected clients. On gap, `ResyncRequired`, or reconnect, client fetches a new snapshot. The Service retains its Engine subscription while UI clients are disconnected; client connection changes do not unsubscribe Engine observers or pause service state/diagnostics ownership. Replaceable statistics and snapshots may be coalesced; peer/lifecycle/error events are retained within the bounded queue. If even non-coalescible events overflow, disconnect and require resynchronization rather than unbounded memory growth.

The initial Service bridge emits `NodeStateChanged` and `DiagnosticsChanged` for Engine lifecycle changes, `SettingsChanged` after a persisted settings revision, and a bounded-cadence `Heartbeat`. Event queues are per client. A client that exceeds its bounded queue is disconnected and must resubscribe and fetch a fresh snapshot.

Statistics default to at most one aggregate update per second. No event includes PCM or per-packet payload data.

## 8. Authentication and authorization

Use a restrictive pipe DACL granting access only to the service SID, administrators, and intended interactive users. Server obtains and validates client token/SID/session. The client cannot authenticate using an embedded static secret. In multi-user sessions, the machine node is shared; settings mutation is limited to the configured local operator/admin policy, while ordinary users may read status and issue allowed stream commands. The UI displays whether an operation was denied due to role.

Pipe access is not equivalent to trust: validate all fields, rate-limit mutations, cap concurrent clients, and close abusive clients. The service never impersonates a client to open arbitrary files or devices. Privileged update/install operations use the signed elevated installer path.

## 9. Connection lifecycle and timeouts

```text
Disconnected -> Connecting -> Handshaking -> Synchronizing -> Subscribed -> Connected
      ^                                                    |
      +---------- reconnect with backoff <--- Closed <-----+
```

Handshake deadline is 5 seconds. An incomplete frame has a 10-second read deadline. A request deadline is command-specific as above. On service shutdown, stop accepting connections, send `ServiceStopping` if possible, allow at most 2 seconds for clients to drain, then close. Broken pipe, timeout, invalid frame, or UI process termination does not stop Engine work. Client reconnect uses exponential backoff with jitter, repeats version negotiation/subscription, and obtains a fresh snapshot. The Service continues node lifecycle, discovery, Control Plane, timers, audio, and authoritative state throughout UI disconnect/reconnect.

## 10. JSON vs MessagePack decision

JSON is selected because IPC is low-rate local control/state traffic, not audio. Readability, field names, supportability, and simple System.Text.Json/.NET interoperability matter more than saving a few bytes or parse cycles. MessagePack is a reasonable future encoding for a measured high-volume use case, but it adds opaque diagnostics and stricter schema discipline. Any future encoding requires explicit handshake negotiation and a new supported encoding identifier; v1 JSON remains supported for its compatibility window.

## 11. Conformance requirements

Independent implementations must agree on framing, JSON UTF-8, bounds, required fields, version selection, request correlation, and error codes. Conformance validation covers fragmented reads, multiple frames in one read, malformed/oversized frames, reconnect epochs, event gaps, slow clients, authorization, and graceful service shutdown. IPC versioning is independent from both network protocol versions.


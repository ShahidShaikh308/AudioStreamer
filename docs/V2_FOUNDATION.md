# AudioStreamer v2 .NET Foundation

**Status:** Engine, Windows IPC, WASAPI adapters, Audio v3 transport, Control v1 Engine policy, Windows UDP transport adapter, Engine-owned peer registry, IPC peer/session operations, and a Milestone 5B WinUI IPC client experience are implemented. The UI has a Mica shell, dashboard/device/session/statistics cards, keyboard navigation, diagnostics summary export, sectioned settings, and tray navigation. The global “Search or run commands” control and Ctrl+K shortcut have been removed. Installer and updater remain unimplemented. A full Windows-to-Windows LAN session, .NET build, and interactive WinUI validation have not yet been completed.

## Projects and dependencies

```text
AudioStreamer.Engine.Tests ───────────────► AudioStreamer.Engine
AudioStreamer.Audio.Transport.Tests ──────► AudioStreamer.Audio.Transport ─► AudioStreamer.Engine
AudioStreamer.IPC.Tests ─────────────────► AudioStreamer.IPC ─────► AudioStreamer.IPC.Contracts
AudioStreamer.ConsoleClient ─────────────► AudioStreamer.IPC ─────► AudioStreamer.IPC.Contracts

AudioStreamer.Service ────────────────────► AudioStreamer.Engine
          │
          ├───────────────────────────────► AudioStreamer.Platform.Windows ─► AudioStreamer.Engine
          ├───────────────────────────────► AudioStreamer.Platform.Windows.Audio ─► AudioStreamer.Engine
          │                                      └───────────────────────────────► AudioStreamer.Audio.Transport
          └───────────────────────────────► AudioStreamer.IPC ──────────────► AudioStreamer.IPC.Contracts

AudioStreamer.Engine ─────────────────────► AudioStreamer.Control.Protocol
AudioStreamer.Platform.Windows ───────────► AudioStreamer.Engine
    implements IControlTransport
```

`AudioStreamer.Engine` targets portable `net9.0`. It owns the stable `IEngine` API, immutable `EngineOptions`, typed results, lifecycle and Control Plane coordinators, bounded event subscriptions, and configuration coordination. It references the Control Protocol v1 library and Engine-defined transport contracts; it does not reference Windows APIs, SCM, UI, IPC, or concrete socket implementations.

`AudioStreamer.Platform.Windows` implements machine-wide settings persistence using a versioned JSON document under `%ProgramData%\AudioStreamer`, and implements Engine `IControlTransport` with Windows UDP sockets and network-change notifications. Settings writes use a temporary file and replace the destination after serialization. The adapter enforces a bounded configuration file size and validates writes; Control v1 policy remains in Engine.

`AudioStreamer.Service` targets `net9.0-windows`. `Program` is the composition root: it sets up structured JSON console logging and a bounded rolling JSONL file sink under ProgramData, registers the Windows Service lifetime, settings/audio adapters, `WindowsControlTransport`, Engine, `NodeHost`, and then `IpcHostedService`. `NodeHost` starts and stops the Engine; Engine starts/stops Control policy and transport in order. Host shutdown stops IPC first; Engine quiesces Control commands, sends GOODBYE, stops the transport adapter and its workers, then stops audio sessions. No UI lifetime exists in this phase.

Service `AudioTransport` configuration supplies the local bind address/receive port, PCM format, and bounded queue sizes. `ControlPlane` configuration supplies its bind address/port, heartbeat/peer/session timeouts, peer bound, and outbound queue bound. Audio sender destinations are resolved from discovered peer HELLO/heartbeat state; no destination IP is manually configured.

`AudioStreamer.IPC.Contracts` is a contracts-only library of immutable, versioned JSON DTOs. `AudioStreamer.IPC` owns bounded length-prefixed JSON framing, Windows local-only pipe creation/ACLs, client/server sessions, handshake, request correlation, per-client bounded queues, and event delivery. It has no Engine reference; `EngineIpcRequestHandler` maps allow-listed requests and Engine domain events through `IEngine`. IPC exposes peer details, Control statistics, stream state, settings, diagnostics, StartStream/StopStream, local Pause/Resume, subscriptions, and heartbeat.

Package versions are centralized in `Directory.Packages.props`; common compiler settings are in `Directory.Build.props`.

## Engine API and current capability set

The `IEngine` surface retains the v2 architecture's operation names and typed result model. Lifecycle, stable node identity, settings revision checks, status/snapshot/version/diagnostic queries, and bounded event subscriptions are implemented. Internal managers now own node lifecycle, settings, the authoritative peer registry, explicit session state, device catalog access, fixed-counter statistics, and process diagnostics. Audio streaming resolves peers from discovery and composes the Windows audio runtime when registered; session transitions and lane controls route through Engine ports. Device enumeration and selection require an injected platform catalog. Full LAN behavior is not yet validated.

Initial Engine lifecycle:

```text
Stopped ──StartAsync──► Starting ──settings loaded──► Running
   ▲                        │                           │
   │                        └──startup failure──► Faulted
   └────────────StopAsync── Stopping ◄───────────┘
                                                  │
                                                  └──RestartAsync──► Restarting ─► Running
```

`StopAsync` is idempotent and a stopped Engine can start again until its DI-owned instance is disposed. The Engine event bus serializes event sequence assignment and uses bounded per-subscriber channels. StateStore is a critical consumer; external slow subscribers are detached after the configured backpressure deadline. Disposal completes the bus and drains StateStore before releasing it. Audio and network workers do not yet exist, so their shutdown policy remains for the corresponding implementation phase.

## Configuration and logging

The Engine owns canonical settings and revision conflict handling. It loads settings through `IConfigurationStore`; when no settings exist it creates and persists a stable Node ID and default node name. Settings are validated before becoming current. Updates validate the expected revision, persist first, then publish the new in-memory snapshot. The Windows adapter owns filesystem operations only.

The Service initializes `Microsoft.Extensions.Logging` before hosted services start. Structured logs go to JSON console output and the bounded rolling JSONL sink (8 MiB per file, 14 retained files, bounded queue, redaction of sensitive structured fields and their formatted values). It logs exception types rather than exception messages/stacks at the process boundary. The file sink is best-effort and drops new records when its bounded queue is full; dropped-record count exposure remains an observability follow-up.

## Tests and verification

`AudioStreamer.Engine.Tests` covers idempotent start, stable identity loading, idempotent stop, restart after stop, pre-start cancellation, settings revision conflicts, event projection, session transitions, peer capacity/expiry, injected device catalogs, and explicit unavailable capabilities. `AudioStreamer.IPC.Tests` covers frame serialization/fragmentation, malformed and oversized frames, incomplete-frame deadlines, handshake mismatch, concurrent/multiple clients, subscriptions, reconnect epochs, request cancellation/timeouts, and malformed-client isolation. `AudioStreamer.Platform.Windows.Audio.Tests` covers PCM conversion/mixing, bounded buffers, ring buffering, clock behavior, and capture lifecycle/overrun handling. `AudioStreamer.Audio.Transport.Tests` covers the Python canonical vector, parser limits, packetization, bounded queue, sequence wrap/restart, and UDP loopback. Python's `audio_plane/tests/test_protocol_dotnet_compat.py` exchanges datagrams with the .NET compatibility executable in both directions. Use:

```powershell
dotnet test AudioStreamer.sln
```

The current workspace does not have the .NET SDK available on `PATH`; build, test, and format execution remain to be confirmed on a .NET 9 SDK host. Control v1 C# tests include Python-generated message vectors; service registry tests cover updates and expiry. Full Python/.NET exchange and Windows LAN/socket recovery validation remain outstanding. Control v1 u64 IDs map from local Engine GUIDs using SHA-256; remote IDs use a namespaced synthetic GUID for IPC routes. A collision observed in the registry is rejected; the wire's 64-bit identity limit prevents a mathematically injective mapping from 128-bit GUIDs.

## Phase boundary

Audio Protocol v3 remains wire-compatible with the Python reference. The Windows Service owns Control v1 UDP discovery, stream negotiation, and peer recovery; Engine peer state receives immutable updates through an internal sink, and the audio runtime receives discovered peer endpoints through Engine state. IPC Start/Stop uses correlated Control v1 status with a configured timeout. The Windows audio runtime shares a UDP receiver across routes and routes datagrams by source IP into bounded adaptive reorder/playout queues. IPC exposes session snapshots, transitions, pause/resume, audio events, statistics, and diagnostics. The Milestone 5B UI client consumes those operations through JSON Named Pipe IPC, reconnects and resynchronizes, and keeps the UI lifetime separate from the Service. Its charts use only server-reported aggregate counter samples, including reported average latency (not one-way LAN latency), and preserve missing samples as unavailable. Audio-device IPC, log paging, Windows toast activation, update installation, service-control commands, Windows-to-Windows LAN, service restart, and long-duration streaming have not yet been proven or implemented. The UI is unpackaged; toast notifications are not presented as a working feature. See [UI_SPECIFICATION.md](UI_SPECIFICATION.md) and [IPC_PROTOCOL.md](IPC_PROTOCOL.md) for current capability limits.

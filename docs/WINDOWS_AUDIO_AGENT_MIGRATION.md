# Windows Audio Agent Migration Blueprint

Status: implementation blueprint and migration record. Milestones 1 and 2 plus the audio cutover (device catalog, playback, capture, PCM, and runtime substitution) are implemented. Windows interactive hardware and installer rollout validation remain release gates. The pre-migration test baseline was 132 tests.

### Implementation status

Milestone 1 added the headless `AudioStreamer.AudioAgent` executable, versioned message contracts and a fixed binary frame codec, transport-independent connection/client/server interfaces, three independent asynchronous local channels using the recommended initial transport, handshake/heartbeat/reconnect support, structured lifecycle logging, and focused protocol tests. Milestone 2 integrated the Service supervisor, active-session process launcher, and server ACL for the AudioStreamer service SID plus the current interactive identity. The audio cutover now uses the Agent for enumeration, notifications, capture, playback, and PCM; the existing Windows runtime continues to own Engine lane orchestration and UDP routing.

The Agent now handles handshake, heartbeat, device enumeration and notifications, endpoint selection validation, capture/playback lifecycle, PCM transfer, and device-change recovery. Control and the two binary PCM channels remain independent. Service-side runtime orchestration and UDP routing are unchanged; the capture/playback/catalog ports are implemented by Agent-backed adapters.

At the end of Milestone 2, the Service-owned `AudioAgentSupervisor`, active interactive-session process launcher, automatic process/IPC/heartbeat monitoring, bounded restart attempts, read-only `GetAgentStatus` request, per-session Agent single-instance behavior, and Agent payload publication were in place, while the existing Windows audio runtime remained active and sent no audio messages. Milestone 3 then moved native audio ownership into the Agent.

**Milestone 2 security/deployment decision:** the existing LocalService identity cannot call `WTSQueryUserToken` to create an agent in an interactive user session. The installer therefore configures the Service as LocalSystem and enables the `AudioStreamer` service SID (`ServiceSid="unrestricted"`). The supervisor launches the active console session when present, otherwise an active WTS session, using the user token and environment. The agent pipe ACL grants the configured `NT SERVICE\AudioStreamer` SID plus the current interactive identity. This is a privilege increase for the Service and must be reviewed as part of release security validation. No UI logon task or separate Windows Service is introduced.

## 1. Current architecture

The solution already has a deliberate platform boundary. `AudioStreamer.Engine` owns `Engine`, `SessionManager`, device policy, discovery, node and peer state, control integration, and engine events. `SessionManager` calls the internal `IAudioSessionRuntime` port for start, stop, endpoint update, pause, and resume. The Windows adapter registers `WindowsAudioSessionRuntime` as that port. The runtime creates per-lane DI scopes, starts `IAudioCapture` or `IAudioPlayback`, runs the UDP sender/receiver and PCM conversion/pump workers, and publishes platform events. Thus UDP and PCM routing currently live in the Windows audio adapter runtime, not in `SessionManager`.

`IAudioCapture`, `IAudioPlayback`, `IAudioDeviceCatalog`, `IAudioDeviceManager`, `AudioFrame`, `AudioFormat`, `AudioEndpointSnapshot`, and device-change records are public Engine contracts in `src/AudioStreamer.Engine/Contracts/AudioPlatformContracts.cs`. `IAudioSessionRuntime` is internal, declared in `src/AudioStreamer.Engine/Managers/SessionManager.cs`; its shape remains unchanged. The Agent-backed Service adapters implement the existing capture, playback and catalog ports. NAudio capture/playback, endpoint enumeration and notifications are compiled by `AudioStreamer.AudioAgent`; the Service-side audio project contains no NAudio dependency or WASAPI activation code.

The Windows service host is `src/AudioStreamer.Service/Program.cs`. It configures Windows Service hosting, logs, Windows configuration and audio, then the Audio Agent supervisor, Engine/`NodeHost`, and existing service/UI IPC. Hosted-service ordering starts the supervisor before `NodeHost` and stops it after `NodeHost`. Existing UI/service IPC uses `AudioStreamer.IPC` framing and the contracts in `AudioStreamer.IPC.Contracts`; it is separate from the service/agent channel. The installer packages the Agent beside the Service and configures the Service identity required for interactive-session launch.

## 2. Proposed architecture

Keep WinUI, existing IPC, Service, Engine, SessionManager, control protocol, discovery, UDP, and stream state in their current locations. Replace only the Windows service audio platform adapter path with a client adapter backed by a per-interactive-user `AudioAgent.exe` process. The agent hosts the existing WASAPI device, capture, playback, and notification code. The service owns `WindowsAudioSessionRuntime` orchestration and UDP; that runtime calls an `IAudioAgentClient` for device operations and PCM I/O instead of resolving local WASAPI objects.

The Service launches AudioAgent in an active WTS user session through `WTSQueryUserToken` and `CreateProcessAsUser`; session selection and the required LocalSystem identity are implemented as described above. The authenticated local named-pipe transport remains private to the Service/Agent boundary. The Agent does not instantiate Engine, discovery, control, UDP, or routing services. It currently references the Engine assembly to share its public audio contracts and references the Windows audio utility assembly; this creates an assembly-level layering dependency documented in Section 15.

## 3. Projects and files

Existing projects that must change:

| Project | Files indicated by current code | Change |
|---|---|---|
| `AudioStreamer.Platform.Windows.Audio` | `WindowsAudioSessionRuntime.cs`; `WindowsAudioServiceCollectionExtensions.cs`; `AgentAudioAdapters.cs`; `WindowsAudioStreamOptions.cs`; `.csproj` | Keep runtime/UDP orchestration and Engine contracts intact; register agent-backed capture, playback and catalog adapters. No WASAPI source or NAudio dependency is compiled into this project. |
| `AudioStreamer.Service` | `Program.cs`; new `AudioAgentSupervisor.cs`; `EngineIpcRequestHandler.cs`; `AudioStreamer.Service.csproj` | Register supervision before Engine starts, launch in active WTS session, expose agent status additively, and stop after `NodeHost`. |
| Solution | `AudioStreamer.sln` | Add agent and any contract/transport projects. |
| Installer | `installer/Package.wxs`; `.github/workflows/windows-rc.yml` | Publish Agent beside Service, use LocalSystem plus the AudioStreamer service SID for WTS user-session launch and pipe ACL. |
| Audio tests | `tests/AudioStreamer.Platform.Windows.Audio.Tests/*`; `tests/AudioStreamer.AudioAgent.Tests/*` | Preserve the existing mockable WASAPI and runtime tests in the Agent assembly; add fake-client adapter, protocol/PCM and Agent command/device-recovery tests. |
| Service tests | existing `tests/AudioStreamer.Service.Tests` plus new lifecycle/pipe tests | Test startup ordering, supervision, stop and recovery using injected process/pipe abstractions. |
| IPC tests | existing `tests/AudioStreamer.IPC.Tests` applies only if shared framing primitives are intentionally reused | Do not modify the UI/service IPC contract. Add dedicated agent protocol tests. |

No Engine source change is required by the proposed boundary. In particular, `AudioPlatformContracts.cs`, `SessionManager.cs`, `EngineServiceCollectionExtensions.cs`, and `DeviceManager.cs` should remain intact. This preserves public Engine APIs and existing Engine tests. The actual constructor signatures, `InternalsVisibleTo`, build packaging target, and any deployment pipeline scripts should be rechecked at implementation time; no unrelated source change is justified by current dependency flow.

## 4. Interfaces

### Existing interfaces

- Preserve public `IAudioCapture`, `IAudioPlayback`, `IAudioDeviceCatalog`, `IAudioDeviceManager`, `IAudioClock`, `IAudioBufferPool`, `IAudioFormatConverter`, `IAudioMixer`, `IAudioLatencyProvider`, and `IAudioDiagnostics` APIs.
- Keep the internal `IAudioSessionRuntime` shape and behavior. Change only its concrete Windows implementation's dependencies. `SessionManager` remains the transition/state owner.
- The local agent should implement the low-level audio behavior corresponding to `IAudioCapture`, `IAudioPlayback`, and endpoint enumeration/notification. Those Engine contracts should not be serialized directly; agent wire DTOs must be versioned and independent from CLR layout.

### New service-side ports

Add, preferably in a small shared `AudioStreamer.AudioAgent.Contracts` project:

- `IAudioAgentClient`: `Connect/Health`, endpoint snapshot retrieval, endpoint selection validation, capture start/read/pause/resume/stop, playback start/write/pause/resume/stop, and device-change subscription. Async methods take cancellation tokens and return explicit result/error DTOs.
- `IAudioAgentConnection` or equivalent internal transport abstraction to permit deterministic transport tests.
- `IAudioAgentSupervisor`: ensure agent for the correct user session, report process/connection health, request graceful stop, restart with bounded backoff, and dispose.
- Injectable `IAudioAgentProcessLauncher` and `IAudioAgentPipeConnector` to test security/session launch and reconnect policy without real processes.

Do not expose raw native handles across the process boundary. Avoid sharing the same interface assembly between service and agent if that would introduce Engine/UDP dependencies; the contract assembly should contain only wire DTOs and protocol definitions.

## 5. New projects

Existing projects `src/AudioStreamer.AudioAgent.Contracts`, `src/AudioStreamer.AudioAgent.Transport`, and `src/AudioStreamer.AudioAgent` provide wire contracts, transport, and the headless Windows executable. The Agent executable owns the NAudio dependency and WASAPI source. `tests/AudioStreamer.AudioAgent.Tests` covers framing, PCM codec and command handling; `tests/AudioStreamer.Platform.Windows.Audio.Tests` covers Service adapters and the existing WASAPI behavior. No additional project was required for this milestone.

## 6. IPC contract and transport recommendation

This channel is private service-to-agent IPC, distinct from existing UI/service IPC. Select the transport against the requirements in this section; do not make the transport choice part of Engine or wire DTO semantics. The current recommendation is three independent asynchronous local channels: one control channel, one Agent-to-Service capture PCM channel, and one Service-to-Agent playback PCM channel. For this repository's current .NET/Windows deployment and modest PCM rate, use asynchronous local named pipes as the initial transport. This is a measured engineering choice based on the expected load, mature .NET support, disconnect handling, and configurable ACLs—not a requirement that prevents a later transport substitution. Keep the client/server and stream interfaces transport-neutral so tests can use in-memory fakes and a later benchmark can justify another transport.

The applicable alternatives are: named pipes (recommended initial implementation: good throughput for current PCM, async streams, ACL support, modest implementation and deployment complexity); shared memory plus synchronization (lowest copy overhead and highest potential throughput, but more complex ownership, synchronization, bounds, and process-crash recovery; use only if measured latency/CPU/allocation results require it); local RPC (`ncalrpc`, robust Windows identity/security and a good control request/reply fit, but native IDL/stub/tooling and call-oriented payload handling add complexity for sustained PCM); out-of-process COM (high integration and marshaling/deployment complexity without a material advantage for this data path); and AF_UNIX (supported by modern Windows/.NET and viable, but does not remove framing/backpressure needs and is less aligned with the service/session ACL model here). Loopback TCP is not recommended absent a future remote-agent requirement. Windows supports file mapping for efficient shared data, but requires explicit synchronization; named pipes support asynchronous operations and ACLs. These tradeoffs are qualitative; no repository benchmark comparing transports exists, so validate the initial choice with representative Windows measurements before considering shared memory.

Implement the IPC transport recommended by this migration document. The transport must satisfy the documented latency, throughput, security, and maintainability requirements. Keep the control, capture PCM, and playback PCM channels independent so control messages cannot queue behind audio frames. Transport replacement must not change the message contract, Engine API, or lifecycle semantics.

**Implemented transport:** the existing replaceable `IAgentTransport` / `IAgentServer` abstraction currently uses three independent asynchronous named-pipe byte streams, protected by the configured service-SID and interactive-user ACL. Control request/response payloads use versioned JSON inside the bounded binary frame; PCM payloads use a fixed little-endian binary header followed by exact interleaved PCM bytes (no JSON/base64). Playback PCM is fire-and-forget: normal frames have no response or round trip; stream start/stop/pause/resume remain correlated control operations. The bounded Agent playback ring drops the newest frame when full and reports the overrun through the existing asynchronous platform-event path. The transport remains replaceable through DI. No cross-transport benchmark has been run, so named pipes remain the initial implementation, not a performance claim.

For the selected transport, use bounded binary framing; avoid JSON/base64 for PCM. A frame header should contain magic, protocol major/minor, message kind, flags, request/correlation ID, payload length, and for PCM sequence/stream ID where applicable. Enforce maximum control and PCM frame sizes before allocation, validate all enum/length/format fields, and close on malformed framing. Negotiate protocol version and capabilities at connection start. Use explicit backpressure and independent readers/writers for each channel.

Required operations and DTO families: `Hello/Ready` (version and agent instance ID); `Heartbeat`; `EnumerateDevices/DeviceList`; `SelectDevices/SelectionResult`; `WatchDeviceChanges/DeviceChanged`; `StartCapture/OperationResponse`; `CapturePcm` (stream ID, sequence, timestamp, format, frame count, payload); `StopCapture`; `PauseCapture`; `ResumeCapture`; `StartPlayback/OperationResponse`; `PlaybackPcm` (same explicit PCM metadata, fire-and-forget); `StopPlayback`; `PausePlayback`; `ResumePlayback`; `Shutdown`; and `Error` (stable safe code, retryable bit, correlation ID, bounded safe message). Operations that mutate one stream carry a service-generated stream ID and are idempotent where practical. Duplicate start/stop requests must not create duplicate native clients.

Represent PCM as tightly defined little-endian interleaved bytes with explicit supported `AudioFormat`, frame count, and timestamp units. Preserve current `AudioFrame` owner semantics inside each process only. On wire, copy from/to bounded pooled buffers; sender retains ownership until write completion and receiver owns its independently allocated buffer. Add monotonically increasing per-stream sequence numbers, queue limits, and a defined overflow policy (drop oldest or reject newest) chosen to match latency goals and surfaced as a metric. Do not change existing UDP protocol v3 packetization.

## 7. Lifecycle, startup, and shutdown

### Service lifecycle and startup

The agent supervisor must be registered as a hosted service with ordering guaranteed before `NodeHost.StartAsync`. Current hosted services are registered `NodeHost` before IPC host; do not rely on an accidental order after adding more services. Use one explicit bootstrap hosted service or a hosted-service dependency so the agent is launched and pipe handshake completed before Engine start. Then Engine starts as today and existing IPC starts as today. Initial endpoint enumeration must fail as a device capability error without unnecessarily killing the whole service if the agent is temporarily unavailable; decide this against existing Engine startup semantics before coding.

Proposed sequence: service host config/logging; validate agent executable/configuration; resolve active console/user session and launch agent securely; create pipe with restrictive ACL and expected service identity; authenticate/handshake and ping; register client event stream; start Engine/NodeHost; start existing UI/service IPC. The AudioAgent supervisor and active-session launcher were introduced in Milestone 2. Device-change and platform-event notifications are forwarded over the control channel, and Service event handling refreshes Engine's existing device snapshot and republishes metadata-only audio events.

### Shutdown

Stop accepting new UI/service requests (or mark service stopping); stop Engine, which clears sessions and calls runtime stop; runtime stops UDP lanes and asks agent to stop capture/playback; await PCM pump termination; dispose pipe client; ask agent to exit; wait bounded interval; terminate only the agent process instance launched by this supervisor if graceful exit fails; dispose supervisor. Ensure hosted-service ordering puts audio-agent shutdown after Engine `NodeHost.StopAsync`, not before it. Existing host has 30-second shutdown timeout, so the audio stop budget must fit within it.

### AudioAgent lifecycle

Agent starts in the user session, initializes structured EventSource logging and the pipe server, then initializes endpoint notification registration and responds to commands. Native clients are created per requested capture/playback stream and stopped idempotently. Device changes and audio lifecycle/diagnostic events are metadata only. On Service/Agent transport reconnection, active operations are replayed by stream ID; a fresh Agent process recreates the native clients. Device notifications restart affected active endpoints. Agent shutdown cancels notification and PCM workers, stops all native clients, and exits.

## 8. Process supervision and failure recovery

Service supervises only its launched agent process and pipe. Track process exit, heartbeat timeout, pipe EOF, protocol fault, and audio-operation failure separately. Use capped exponential reconnect/restart backoff with jitter and a circuit-breaker threshold; avoid tight restart loops. On loss, mark affected audio lanes failed/recovering through the existing runtime result path and publish existing safe audio error events. Do not change Engine state transitions or control protocol. After reconnect, re-enumerate endpoints; do not automatically resume streams unless current Engine recovery semantics explicitly request a new `StartAsync`. Endpoint update continues to update the service-owned UDP sender/receiver; it should not cause WASAPI restart unless device selection changes.

A failed `StartCapture`/`StartPlayback` returns `TransportUnavailable` or `CapabilityUnavailable` through existing `EngineResult` mapping; start failure is reported by `SessionManager` as Failed. Stop must be idempotent and best effort, but incomplete stop must be logged. Cancellation must cancel the pending request and clear its correlation entry without leaving the agent operation orphaned. Disconnect while PCM is in flight discards partial frames and disposes local buffers. A bounded reconnect must not block Engine/UI IPC indefinitely.

## 9. Ownership and concurrency

- **Audio devices/native handles:** AudioAgent only. The WASAPI implementations and `WindowsAudioDeviceManager` source are under `src/AudioStreamer.AudioAgent/Audio`; the Service audio project does not compile those files and no longer references NAudio.
- **PCM source/sink hardware buffers:** AudioAgent. Capture PCM is copied into outbound pipe frames; playback pipe frames are copied to Agent-owned buffers before WASAPI submission.
- **PCM routing, conversion, UDP:** Service-side `WindowsAudioSessionRuntime`/existing `AudioStreamer.Audio.Transport`; preserve current responsibilities and v3 packets.
- **Thread/task ownership:** Agent owns native callback threads, device-notification worker, per-direction WASAPI workers, pipe server loops, and bounded PCM queues. Service owns supervisor, pipe request/response loops, existing capture-to-UDP and UDP-to-playback pumps, Engine/session operations. No synchronous wait from a WASAPI callback on pipe I/O.
- **Synchronization:** One serialized control-command dispatcher per agent connection, correlation map for replies, separate bounded PCM queues/readers, per-stream state lock or actor. Preserve serialized session transitions via current `SessionManager` and runtime `_gate`; do not hold a global pipe lock while waiting for PCM or device callbacks. Device-change notifications are coalescible snapshots; PCM is ordered per stream.
- **Backpressure:** bounded buffers on both sides; explicit write timeout / queue-full response and counters. Never allow pipe disconnect or slow UDP consumers to grow memory without bound.

## 10. Logging and security

Keep current service JSON console and rotating file provider. Add structured fields: agent PID/session/user SID (avoid logging credentials), agent instance ID, protocol version, correlation/stream ID, operation, device ID where appropriate, safe error code, queue depth, reconnect attempt, and duration. Agent writes to its own user-appropriate log location or emits structured logs over a bounded diagnostic channel; never log PCM. Preserve existing `AudioPlatformEvent` metadata-only rule. Do not expose raw COM exception text to UI; log detailed exception in the owning process and return stable safe error codes.

Create the pipe with an explicit ACL that admits only the intended AudioStreamer service identity and the selected interactive user identity. Authenticate both ends using Windows token/session information and a launch nonce or equivalent protected handshake; pipe name secrecy is not authentication. Restrict executable and configuration ACLs, quote executable paths safely, avoid arbitrary command-line interpolation, validate message sizes, and prevent another local process from impersonating the agent. Document elevation/UAC and multi-user session behavior. Threat model must address user switching, RDP/disconnected sessions, multiple simultaneous users, stale pipes, and agent binary replacement.

## 11. Testing strategy

Preserve existing Engine, protocol, UI, discovery, and IPC tests. Retain PCM converter/buffer and UDP protocol tests. The following focused coverage is now implemented:

1. Contract codec golden tests: version handshake, framing, malformed lengths, invalid formats, partial reads/writes, max-size enforcement.
2. Pipe client/server integration tests under the same user, plus ACL/identity validation tests on supported Windows CI.
3. Agent fake-device tests for enumerate/select/notifications and start/stop/pause/resume idempotence.
4. PCM exact-byte/format/timestamp/sequence tests, bounded queue behavior, backpressure, disconnect mid-frame and ownership/disposal tests.
5. Service supervisor tests with fake launcher/pipe: startup order, process exit, timeout, bounded retries, reconnect, shutdown escalation and no orphan process.
6. Runtime tests using fake Agent adapters: preserve UDP lane, pause/resume, update endpoint, failure mapping, multi-route/shared receiver behavior, cleanup rollback. Existing behavior tests remain green; new adapter and Agent command tests exercise capture/playback, PCM, device notifications and restart handling.
7. Windows hardware smoke test under interactive logged-on user for capture loopback, playback, device enumeration/change, service restart, user logoff and agent restart. **Not run in this environment.**
8. Full solution build and all .NET tests passed after the code cutover (0 build warnings/errors). Installer install/upgrade/repair/uninstall and representative latency/CPU/allocation benchmarks remain release validation; no benchmark results are claimed.

## 12. Compatibility, deployment, and installer

Public Engine APIs, UI APIs, Control Protocol, existing UI/service IPC, discovery behavior, UDP protocol, and session state transitions remain stable. Keep `WindowsAudioStreamOptions` semantics initially; service may pass selected device IDs and format to agent operations. Wire protocol is new and independently versioned. Agent/service versions must be shipped atomically or negotiate compatible protocol ranges. Existing service installations need upgrade logic for the new executable and launch integration.

The current installer harvests the published AudioAgent payload beside the Service and configures the LocalSystem service identity and AudioStreamer service SID required by WTS token launch. The Agent publish contains NAudio/WASAPI dependencies; the Service publish does not. Remaining installer validation includes MSI install/upgrade/repair/uninstall, signature verification, active-console and RDP session behavior, and cleanup of an orphaned Agent after abrupt Service termination. AudioAgent is launched and supervised by the Service; no per-user scheduled task or separate Windows Service is used.

## 13. Risks and rollback

Key risks: the LocalSystem identity increases Service privilege; Windows session/token launch behavior and active-console/RDP session selection need interactive deployment validation; service-SID pipe ACL errors; user switching/multiple-session ambiguity; added PCM copy and scheduling latency (deferred to PCM milestone); agent crash causing stream interruption; protocol/version skew; installer upgrade leaving orphaned agents; device notifications or endpoint mix-format probing still accidentally occurring in service; and bounded queue settings that either add latency or drop audio.

Rollback is a matched, versioned Service/Agent package downgrade. There is no runtime fallback that activates WASAPI inside the Service. The Service-side WASAPI sources and NAudio dependency have been removed; preserve logs and configuration and do not alter Engine/Control/UI contracts. Define rollback trigger criteria (startup failure rate, pipe disconnects, underruns, latency) before staged rollout.

## 14. Performance implications and future extension

Named pipes add one local IPC hop. PCM encoding writes the protocol and PCM headers directly into the final outbound frame, avoiding an intermediate PCM serialization buffer; incoming payloads are bounded allocations and are copied into owned audio buffers at each process boundary. Playback is fire-and-forget; sending a frame waits only for the cancellable asynchronous transport write, never for an Agent acknowledgment. Capture callback queue overflow drops the newest frame; Agent playback ring overflow also drops the newest frame and increments overrun diagnostics. The Service-side capture queue is bounded to eight frames and drops newest when full. Capture queue underflow waits asynchronously for data; playback device queue underflow outputs silence and reports an asynchronous event. Benchmark end-to-end latency, CPU, allocation rate, queue duration, and drops at current sample rate/format and maximum supported stream count before release. Shared-memory rings could reduce copies later, but introduce ACL, lifetime, crash recovery, and synchronization complexity and are not required for this migration.

Keep protocol capabilities explicit for future device formats, per-stream device selection, capture-source selection, latency reporting, and alternate audio backends. Version additive DTO fields; reserve message kinds; keep control and PCM planes separable. Future extensions must not migrate Engine/session/routing policy into AudioAgent.

## 15. File-level dependency map (implemented)

- `src/AudioStreamer.Platform.Windows.Audio/WindowsAudioSessionRuntime.cs`: keeps UDP routing, conversion, queue pumps, shared receiver, endpoint updates, pause/resume and Engine event behavior. It resolves the existing audio ports, which now resolve to agent-backed adapters.
- `src/AudioStreamer.Platform.Windows.Audio/AgentAudioAdapters.cs`: maps existing `IAudioCapture`, `IAudioPlayback` and `IAudioDeviceCatalog` contracts onto the Agent audio client and owns the Service-side PCM copies.
- `src/AudioStreamer.Platform.Windows.Audio/WindowsAudioServiceCollectionExtensions.cs`: registers the proxies and unchanged runtime; it has no NAudio registrations.
- `src/AudioStreamer.AudioAgent/Audio/Devices/WindowsAudioDeviceManager.cs`: Agent-only endpoint enumeration, mix-format probing, notifications and default endpoint metadata.
- `src/AudioStreamer.AudioAgent/Audio/Capture/WasapiAudioCapture.cs`, `Audio/Playback/WasapiAudioPlayback.cs`, and `Audio/Internal/Wasapi*Backend.cs`: Agent-only native capture/playback and their mockable WASAPI backends.
- `src/AudioStreamer.AudioAgent/AgentAudioCommandHandler.cs`, `AgentWindowsAudioServiceCollectionExtensions.cs`, and `AgentEventPublisher.cs`: Agent command dispatch, native stream ownership, device recovery, and metadata event forwarding.
- `src/AudioStreamer.AudioAgent.Contracts/AgentContracts.cs`, `AgentProtocolCodec.cs`, and `TransportAbstractions.cs`: versioned contracts, PCM codec, and transport-neutral client/server ports.
- `src/AudioStreamer.AudioAgent.Transport/AgentClient.cs`, `AgentServerWorker.cs`, and `NamedPipeAgentTransport.cs`: three independent transport channels, operation request/response handling, heartbeat, reconnect, and fire-and-forget PCM delivery.
- `src/AudioStreamer.Service/Program.cs`, `AudioAgentSupervisor.cs`, and `AudioAgentDeviceChangeService.cs`: Service DI, process/session supervision, Engine device refresh and event forwarding.
- `installer/Package.wxs` and `.github/workflows/windows-rc.yml`: harvest the Agent payload beside the Service; installer code was already updated in Milestone 2 and remains the packaging path.
- `tests/AudioStreamer.AudioAgent.Tests/*` and `tests/AudioStreamer.Platform.Windows.Audio.Tests/AgentAudioAdapterTests.cs`: protocol/PCM, fake-device command handling, device-triggered restarts, and Service adapter behavior.

`AudioStreamer.Engine/Contracts/AudioPlatformContracts.cs`, `SessionManager.cs`, and Engine APIs remain unchanged. The Agent currently references the existing Engine assembly to reuse its public audio data/port contracts and the Windows audio utility project for buffers/diagnostics; it does not register or instantiate Engine, discovery, control, or UDP services. This assembly-level dependency is a known layering debt. Existing `AudioStreamer.IPC` and `AudioStreamer.IPC.Contracts` are untouched.

## 16. Milestone plan (each increment must build and be testable)

| Milestone | Scope | Independently verifiable result |
|---|---|---|
| 1. Agent shell and transport foundation — **Complete** | Add `AudioStreamer.AudioAgent`, versioned contracts and codec, transport abstractions, three asynchronous local channels, handshake, heartbeat timeout/reconnect loop, structured logging, and tests. No WASAPI migration. | Full solution build succeeded with 0 warnings/errors; full suite passed (132 existing tests plus 6 new AudioAgent tests). Current Service registration and Windows audio runtime remain unchanged. |
| 2. Process launch and supervision — **Implemented** | Add service launcher/supervisor, user-session identity policy, service-SID ACL, status endpoint, Agent singleton behavior, Agent payload publication, and startup/shutdown ordering. Keep current WASAPI runtime unchanged. | Full solution build succeeded with 0 warnings/errors; all 146 tests passed (including 8 deterministic supervisor tests); `dotnet format --verify-no-changes` passed. Interactive Windows MSI/session smoke testing remains a release gate and requires an installed LocalSystem service and interactive Windows user session. |
| 3. Device catalog | **Complete.** Agent enumeration, mix-format probing, endpoint notifications and default metadata; Service catalog proxy and Engine refresh. | Fake-device command and notification tests pass; Service project has no NAudio/WASAPI references. |
| 4. Playback control | **Complete.** Remote start/pause/resume/stop and operation error mapping. | Adapter and Agent command tests cover lifecycle. Hardware smoke remains pending. |
| 5. Capture control | **Complete.** Remote start/pause/resume/stop and capture lifecycle. | Adapter and Agent command tests cover lifecycle. Hardware smoke remains pending. |
| 6. PCM streaming | **Complete.** Independent bounded binary capture/playback channels, metadata, ordering, playback ACK/backpressure and reconnect stream restoration. | Byte-exact codec and command-path tests pass. Performance benchmarking remains pending. |
| 7. Runtime substitution | **Complete.** Existing runtime uses proxies while retaining UDP, conversion, lane, and Engine behavior. | Existing and new .NET tests pass. Interactive multi-route hardware smoke remains pending. |
| 8. Packaging and rollout | **Payload configured; validation pending.** Paired Service/Agent publish and installer harvest are in place. | MSI install/upgrade/repair/uninstall and interactive session validation remain release gates. |
| 9. Remove legacy service WASAPI | **Complete.** WASAPI source is compiled only in AudioAgent; NAudio was removed from the Service audio project. | Source/project dependency audit confirms no Service WASAPI activation code. |

The service launch milestone precedes device and stream migration because the current runtime cannot use an agent before the correct user-session process is supervised. Each milestone should compile independently; hardware-only checks are explicitly identified and should run on an interactive Windows host.

## 17. Milestone 3 implementation notes and deviations

- Control messages use bounded JSON payloads inside the versioned binary frame. PCM is binary and never base64 encoded. The current frame header carries magic, version, channel, message kind, sequence, correlation ID and payload length; PCM stream ID and audio metadata are in the PCM payload header. Flags are reserved bytes rather than a separately modeled header field.
- Playback PCM is fire-and-forget. The Service observes transport write failure/disconnection and receives aggregate overrun information through Agent events; it does not receive per-frame queue acceptance. Control operations retain correlated completion responses.
- The Agent references `AudioStreamer.Engine` for the existing public audio types and `AudioStreamer.Platform.Windows.Audio` for shared buffer/diagnostic utilities. It does not construct Engine or network/control services. Removing these assembly-level dependencies cleanly would require extracting the public audio contracts without changing their assembly identity; that refactor is outside this milestone and is recorded as layering debt.
- `IAudioDeviceCatalog.SelectAsync` continues to validate the selected endpoint, matching its existing behavior; it does not change the Windows default device. Device-change recovery restarts streams whose selected endpoint changed or whose implicit default changed.
- Automated build/tests run without an interactive audio endpoint. Hardware WASAPI capture/playback, service-session switching, installer lifecycle, and performance benchmarks are still unverified release gates.

## 18. Transport cleanup before hardware validation

- Playback PCM now uses fire-and-forget transport writes. The old per-frame `PlaybackAccepted` response, response DTO, and playback serialization semaphore were removed. Start/stop/pause/resume operations still use correlated control responses.
- PCM framing writes the outer protocol header, PCM metadata header, and source PCM directly into one final outbound byte array; the intermediate encoded-PCM array was removed. The receiving side still allocates a bounded payload and copies PCM into the local audio buffer owner.
- Bounded queue policies were verified: WASAPI capture and the Service capture handoff drop the newest frame when full; the Agent playback SPSC ring rejects the newest frame when full; playback underflow emits silence; capture consumers await data. Overrun/underrun reporting remains asynchronous.
- Capture callback failures are forwarded through a bounded nonblocking channel and logged by a background worker. No callback performs IPC, logging, endpoint enumeration, or blocking waits.
- Heartbeat supervision now checks all three channel connections, so loss of a PCM channel triggers a complete client reconnect even if control-channel heartbeats still respond. A focused recovery test covers playback-channel loss.
- Removed unused client-unreachable Ping, Version, and Health commands/DTOs; the live handshake already provides protocol and agent version information, and heartbeat provides liveness. Removed unused legacy operation DTOs and per-frame playback acknowledgment contract.
- Bumped the AudioAgent wire protocol version because removing per-frame responses changes the PCM channel’s interaction semantics. Service and Agent binaries must be deployed as a matched pair; an older v1 peer is rejected during handshake rather than silently stalling on playback.
- The Service/Engine APIs and existing audio runtime orchestration remain unchanged. Remaining release work is hardware, interactive-session/ACL, installer, and performance validation; the current bounded-channel and IPC approach has not yet been benchmarked on supported endpoint hardware.

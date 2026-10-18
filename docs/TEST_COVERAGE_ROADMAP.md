# AudioStreamer Test Coverage Map and Roadmap

**Review date:** 2026-10-13  
**Scope:** Every checked-in automated test project and test source found under `tests/` and `audio_plane/tests/`; production code in .NET v2, Python reference, Android, and ESP32.  
**Status:** Coverage review only. No tests or production code were added or changed.

## 1. Coverage summary

There are seven .NET test projects, one shared Audio v3 fixture, and one Python compatibility test module. The current suite is strongest on deterministic unit/component behavior for codecs, Engine managers, PCM buffers, fake capture/playback adapters, IPC framing, and a limited pipe-server scenario. It does not currently provide broad runtime integration, concurrency/stress/soak, installed-service audio, actual cross-platform Control Plane interoperability, Android tests, ESP32 tests, or WinUI automation.

The Windows release workflow runs the .NET solution tests and the Python/.NET Audio v3 compatibility module. The test project inventory and code assertions below establish what is authored, not what has passed on a supported build agent. This document does not claim a test run.

## 2. Existing test projects and what they cover

| Test project / source | Production code meaningfully exercised | Coverage boundary |
|---|---|---|
| [`AudioStreamer.Engine.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Engine.Tests) | `Engine` start/stop idempotence, stable identity, cancellation before start, stop cleanup despite canceled host token, settings revision conflict/persistence. `EngineEventBus` sequence and `StateStore` latest projection. `SessionManager` transition acceptance/rejection. `PeerManager` capacity, expiration, Control metadata. `DeviceManager` with a fake catalog. Engine default unsupported plane results. | No full DI composition with Windows adapters; no `NodeHost`/Service orchestration; no real audio runtime; incomplete event bus backpressure, subscriber failure, state race, diagnostics/statistics/settings validation, session failure/rollback, or concurrent command tests. |
| [`AudioStreamer.Audio.Transport.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Audio.Transport.Tests) | Audio v3 canonical bytes, a small invalid-header/version set, MTU/frame-aligned packetization, bounded receive queue/drop-oldest, reorder/duplicate, missing/underrun, sequence wrap/restart. Loopback UDP receiver parse/routing and sender packetization/send using an identity converter. | No property/fuzz/boundary suite for every parser field; no sender queue saturation/drop/converted-format path; no socket bind/cancellation/restart/error injection; no race/dispose/route-churn tests; no throughput, jitter adaptation, packet loss/reordering stress, simultaneous lane or audio E2E/runtime tests. |
| [`AudioStreamer.Control.Protocol.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Control.Protocol.Tests) | Control v1 HELLO golden bytes; core message golden bytes and round trips; truncated transaction ID; a small malformed header/version/type set. | No complete malformed/boundary matrix, all text/enum/reserved-bit cases, fuzzing, Python/Kotlin/ESP32 parity, transport behavior, discovery/registry/runtime commands, or timeout/recovery tests. |
| [`AudioStreamer.Platform.Windows.Audio.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Platform.Windows.Audio.Tests) | Pool reuse/clear/return accounting and bounds; `AudioFrame` duration/alignment; PCM conversion/mixing; SPSC ring order/capacity; monotonic clock arithmetic. `WasapiAudioCapture` and `WasapiAudioPlayback` lifecycle through fake backends, queue overrun/underrun and frame ownership. | The actual NAudio/WASAPI backend is not instantiated. There is no Windows endpoint catalog/device notification test, `WindowsAudioSessionRuntime` test, capture-to-UDP-to-remote-playback integration test, Service-account/session test, audio device hot-plug, format/device failure, real-time callback allocation, concurrency, long stream or acoustic test. |
| [`AudioStreamer.IPC.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.IPC.Tests) | Frame codec round trip, fragmented reads, zero/oversized size, malformed/trailing JSON, incomplete-frame timeout. Named-pipe server/client tests for concurrent requests from two clients, subscribe/unsubscribe events, reconnect epochs, handler timeout, client cancellation, malformed client isolation, unsupported version and handshake deadline. | `RequestDispatcher` allow-list/error mapping/role policy is not covered as a production handler; no Windows DACL/token/session authorization test, message payload schema matrix, maximum frame, request flood, client/outbound queue saturation, slow-reader, max-client, heartbeat timeout, event gap/resync, server shutdown/restart race, service integration, or reconnect-during-stream proof. |
| [`AudioStreamer.Service.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Service.Tests) | Control identity conversion; supported legacy TOML migration, unsupported-key report, invalid migration, backup and existing-settings precedence; logger redaction of a sensitive structured property and rendered message. | No `Program`/DI composition, `NodeHost`, `IpcHostedService`, `EngineIpcRequestHandler`, `ServiceCrashBoundary`, SCM lifecycle/recovery, installed ACL, production socket behavior, logger rotation/retention/queue-full/disk-failure, diagnostics collector, or installer transaction tests. |
| [`AudioStreamer.Engine.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.Engine.Tests) | Engine policy and lifecycle; mocked `IControlTransport` regression tests cover HELLO discovery, heartbeat scheduling, peer expiry, correlated outgoing stream routing, GOODBYE, and transport stop during Engine shutdown. | No real Windows transport socket tests, remote command handling, duplicate/stale/malformed traffic matrix, capability rejection, timeout/error rollback, route recovery, network-change/rebind, backoff, queue saturation, concurrency, or soak tests. |
| [`AudioStreamer.UI.Tests`](/C:/Development/Python/AudioStreamer/tests/AudioStreamer.UI.Tests) | `UiPageRegistry`: unique destinations and stable titles. | No tests for any ViewModel behavior, IPC connection/reconnect and snapshot/event reconciliation, settings persistence, command handling, WinUI rendering/layout, navigation interaction, keyboard shortcuts, search, cards/flyouts, tray, clipboard/export, accessibility, theme/scaling, or UI disconnect while streams continue. |
| [`audio_plane.tests.test_protocol_dotnet_compat`](/C:/Development/Python/AudioStreamer/audio_plane/tests/test_protocol_dotnet_compat.py) | Python Audio v3 serializer/parser against one canonical vector; Python→.NET parse and .NET→Python serialization exchange through the compatibility console tool. | Only one canonical packet vector and Audio v3. It does not test malformed/boundary cross-language vectors, Control v1, Android or ESP32 implementations. |

The shared fixture is [`audio_v3_canonical.hex`](/C:/Development/Python/AudioStreamer/tests/ProtocolFixtures/audio_v3_canonical.hex). It is the only cross-runtime canonical protocol fixture discovered.

## 3. Production source with no direct automated coverage

### .NET Service and Control Plane

- `src/AudioStreamer.Service/Program.cs` startup configuration, DI registration and host exit behavior.
- `NodeHost` start ordering, partial-start rollback, quiesce and shutdown ordering.
- `IpcHostedService` event/heartbeat pump restart and stop behavior.
- `EngineIpcRequestHandler` request DTO validation, Engine mapping, error mapping and capability-unavailable behavior.
- `ControlCoordinator` coverage remains partial: remote command handling, route recovery, network-change restart, and internal bounded-channel overflow lack tests. `WindowsControlTransport` socket bind/send/receive, restart, cancellation, and fault mapping also lack tests.
- `ControlRuntimeOptions` configuration validation and boundary values.
- `ServiceCrashBoundary` process/task exception handling.
- Actual Windows Named Pipe ACL creation and client token/session claims; current pipe integration tests exercise same-user connections, not the authorization boundary.

### .NET Engine and adapters

- `SettingsManager`, `DiagnosticsManager`, `StatisticsManager`, `NodeManager` error/degraded paths, and full Engine command/API mapping have only partial or indirect coverage.
- `EngineEventBus` critical/noncritical subscribers, timeout-removal behavior, subscriber disposal races, slow consumers, publish/dispose race and concurrent publishers.
- `StateStore` resynchronization/gap recovery and concurrent projection behavior.
- Windows `WindowsAudioSessionRuntime`, shared receive socket ref-counting, simultaneous sender/receiver lanes, transactional route setup/rollback, pause/resume, stop races and selected endpoint updates.
- Actual WASAPI backend classes, endpoint catalog/device notifications, `AudioDiagnostics`, Windows configuration store error/atomic-save/permission behavior, and adapter recovery.

### IPC and UI

- `VersionHandshake`, `RequestDispatcher`, `ConnectionManager`, `EventPublisher`, `ClientSession`, `ResponseWriter`, `PipeClient`, reconnect coordinator, sequence-gap resync, and authorization do not have isolated exhaustive contracts.
- UI ViewModels and actual WinUI composition have no tests beyond the navigation registry; real UI automation and accessibility checks are absent.

### Other implementations

- Python has no discovered unit suite beyond the single Audio v3 compatibility module. Python queue, UDP sender/receiver, node lifecycle, Control Plane registry/manager/broker, and Windows capture/playback are untested here.
- Android has no checked-in unit or instrumentation test source under `platforms/android`. `AndroidNode`, capture service, MediaProjection permission/consent, Audio v3 codec/transport, packet queue, `AudioTrack`, Control Plane manager/registry, lifecycle and network permissions are untested.
- ESP32 has no native/host test project or board test suite in the repository. Control v1 parser/serializer, registry capacity/expiry, message queue overflow, Wi-Fi reconnect, UDP transport, identity persistence, joystick/UI state transitions and task behavior have no automated coverage.
- The installer, update manifest scripts, diagnostics bundle script and release workflow have no script-level tests. The RC validation script is a smoke procedure, not a test project; it covers MSI install/start, basic IPC, JSONL log parsing, SCM recovery configuration, optional force-kill recovery, then uninstall. It does not cover upgrade/repair/rollback, state/config preservation, failure rollback, signed package verification or full audio behavior.

## 4. Coverage gaps by requested category

### Missing unit tests

- Full Audio v3 and Control v1 validation matrices: min/max lengths and values, every header/payload field, reserved/unknown flags, enum values, UTF-8 limits, invalid IDs, truncations at each offset, sequence arithmetic boundaries, and fuzz/property-generated malformed datagrams.
- Audio sender conversion, packet queue saturation/drop accounting, transport options validation, receive-route update/unregister edge cases, and socket error/restart state.
- Engine manager errors and race contracts: settings load/save failure, device catalog failure/selection, statistics/diagnostics calculations, event backpressure/removal, lifecycle fault transitions, session transaction rollback and pause/resume semantics.
- Service mapping and configuration validation/error handling; Windows pipe ACL/identity policy; logger rotation, retention, flush, disk-full, queue-full, and sink failure behavior.
- All UI Core view models and preferences store, including event-to-snapshot state updates and reconnect behavior.
- Python queue/lifecycle/transport/control modules; Kotlin Android modules; C++ ESP32 modules.

### Missing integration tests

- Full Service host composition: bootstrap config and logging → Engine → Windows audio adapters → ControlRuntime → IPC, including fail-at-each-startup-phase cleanup and Service shutdown.
- Engine → `WindowsAudioSessionRuntime` → capture/playback → Audio UDP sender/receiver → remote peer; current tests exercise codecs/transports and fake device adapters separately.
- `ControlRuntime` ↔ Engine peer registry ↔ IPC route commands using loopback UDP and deterministic peers; current Control protocol tests stop at the codec.
- Windows Service/Named Pipe end-to-end authorization and UI reconnect/resnapshot during an active stream without state mutation.
- Python/Kotlin/ESP32 Control Protocol v1 exchange; Android/Windows Audio v3 exchange in both directions; Windows↔Windows, Windows↔Android, and ESP32 controller command paths on a real LAN.
- Installer first install, upgrade, repair, rollback, uninstall, reboot/auto-start, config/Node ID preservation, ACL validation, and exact published payload verification.

### Missing stress and load tests

- Audio sustained packet-rate load with jitter, burst loss, duplicate/reordered datagrams, full queues, converter workload, simultaneous lanes, and maximum configured peers. Capture queue duration, drop/underrun counts, latency percentiles, CPU, allocations/GC and memory bounds.
- Control discovery/heartbeat storms, malformed UDP floods, peer-capacity pressure, route churn, network address changes and retry/backoff under repeated socket failure.
- IPC max clients, concurrent request workers, request flood/rate behavior, slow readers, outbound/event queue saturation, large-but-valid frames, reconnect storms and bounded memory.
- Engine multi-producer event/statistics load, slow critical/noncritical subscribers and concurrent lifecycle/config commands.
- Log bursts, rotation thresholds, retention pruning, full disk, denied directory access and sustained sink throughput.
- UI high-rate snapshot/event delivery, large peer/session lists, rapid navigation and reconnect while rendering.
- Android thermal/power/GC pressure and ESP32 bounded registry/queue/task-load scenarios.

### Missing soak tests

- No checked-in or recorded 8-hour RC soak or repeated 24-hour stable-release soak. The test plan states these are required.
- No sustained Windows capture→network→playback session with UI open, hidden, disconnected and restarted; no simultaneous sender/receiver or repeated start/stop soak.
- No Android playback capture/AudioTrack or Android↔Windows long-running audio soak.
- No ESP32 controller uptime/Wi-Fi reconnect/discovery soak.
- No measurements for memory/handle/socket/thread growth, queue bounds, GC pauses, drift, underruns, packet loss or recovery over time.

### Missing recovery tests

- Existing coverage: fake adapter cancellation before capture startup; IPC request timeout/cancellation; malformed-client isolation; one configuration migration invalid-input case; and an external RC script that can kill the installed Service process and check SCM restarts it.
- Missing: Engine/NodeHost partial startup failure at every phase; Control socket bind collision and receive/send failure/rebind; heartbeat/peer expiry then rediscovery; network interface change; adapter exception/hang; audio endpoint removal/re-add and selected-device recovery; IPC listener/event pump failure while streaming; IPC reconnect resnapshot during a live stream; repeated SCM crashes/failure limit/reset behavior; settings corruption/disk-full recovery; update failure/rollback; and installer interruption/reboot recovery.
- No test proves that actual audio and discovery continue uninterrupted during UI close, crash, restart, IPC disconnect, or heartbeat loss.

### Missing concurrency tests

- Audio queue concurrent producer/consumer offers/takes/snapshot/dispose; receiver register/update/unregister while datagrams arrive; sender enqueue/destination change/stop; shared receiver lane reference-count races; capture callbacks concurrent with pause/stop/dispose.
- Engine simultaneous Start/Stop/Restart/settings/stream commands, peer updates/expiry, event publish/subscribe/unsubscribe/dispose, and state snapshot consistency.
- ControlRuntime concurrent inbound commands, pending operation completion/timeouts, peer recovery and route mutation, worker cancellation/stop and repeated start/stop.
- IPC simultaneous request workers, request ID tracking under concurrency, response/event writer ordering, disconnect during in-flight command, server stop during handshake/request, and slow-consumer overflow.
- UI dispatcher event ordering, reconnect epochs, late events from old connection, and view-model lifetime/unsubscription.
- No race/stress runner, randomized schedule test, or long-running concurrency suite is present.

### Missing audio tests

- Real WASAPI loopback and playback on supported Windows images under the installed `LocalService`/Session 0 context; before login, after login/logoff, fast user switch, RDP, sleep/resume and default-device changes.
- Physical endpoints, device hot-plug, endpoint enumeration/selection, unsupported formats, resampling/channel conversion under production device formats, device lost/recovery and simultaneous lanes.
- End-to-end audibility, latency measurement, clipping/channel ordering, drift, feedback, CPU/GC and long-run quality measurements.
- Real Android MediaProjection consent/capture restrictions, playback policy, AudioTrack underrun recovery, and phone sleep/background behavior.
- Loopback tests today use fixed local UDP sockets and fake capture/playback sessions; they prove packet-level component behavior, not audio quality or the deployed audio session.

### Missing IPC tests

- Actual Windows pipe DACL inspection and client-token/session/role authorization, including standard user, admin, service, anonymous/guest, other session and unauthorized command cases.
- Full handshake validation (role, process ID, client version, missing/duplicate supported versions, schema minor negotiation), every request schema/allow-list/error mapping, rate limits and mutation authorization.
- Maximum/over-maximum frames, invalid UTF-8, truncation at all framing boundaries, combined frames, heartbeat timeout, max client count, queue saturation/slow reader, event sequence gaps and resync.
- Service integration tests for `EngineIpcRequestHandler` and `IpcHostedService`; current pipe tests use a fake handler and do not exercise the product mapping or Service/Engine survival.

### Missing Control Plane tests

- Mocked-transport Engine tests cover HELLO discovery, periodic heartbeat, peer expiry, correlated outgoing route/status handling, GOODBYE, and shutdown. They do not instantiate the production `WindowsControlTransport` or run real UDP.
- Missing inbound remote command routing, peer address/capability change cases, stale/duplicate packets, transaction timeout/rejection/rollback, desired-route recovery, network-change re-advertisement, retry/backoff, queue overflow, malformed packet flood, and socket lifecycle.
- No cross-implementation vectors or live exchange with Python, Android Kotlin or ESP32 C++. Control codec golden tests only compare some .NET messages to known Python-reference bytes.
- No tests verify controller selection flow → command → sender/receiver state transitions, or confirm the product reports UDP send separately from remote/audible acknowledgement.

### Missing UI tests

- No ViewModel unit tests for `RuntimeStateViewModel`, `SessionsViewModel`, `SettingsViewModel`, `DiagnosticsViewModel`, `IpcConnectionCoordinator`, or `UiPreferencesStore`.
- No UI automation for WinUI startup without Service, connection retry, fresh snapshot/resync, active stream while UI exits/restarts, settings validation, commands and errors, remaining keyboard accelerators, selection/flyouts, tray, theme, window resizing or empty/error states.
- No accessibility checks for screen-reader names, keyboard focus, high contrast, 100–200% scaling, localization readiness or reduced motion.
- No performance tests for dispatcher backlog, event burst handling, virtualized cards, memory growth or responsive rendering while streaming.

## 5. Prioritized test coverage roadmap

This is a coverage plan, not implementation work. Sequence test work by dependency and release risk.

### P0 — Core correctness and release safety

1. **Protocol completeness:** broaden Audio v3 and Control v1 golden/boundary/malformed tests; share fixtures and execute each supported implementation against them. Add Control Kotlin and ESP32 host-test coverage.
2. **Engine contracts:** cover settings/diagnostics/statistics, manager failure paths, event backpressure/resync, session rollback and lifecycle races using deterministic clocks and injected fakes.
3. **Service orchestration:** test `NodeHost` and `IpcHostedService` ordering, partial-start unwind, stop idempotence, timeout behavior, crash boundary, Service+Engine+Control+IPC composition using fake adapters.
4. **IPC authorization and product dispatch:** test Windows pipe ACL/token roles on Windows; exhaustive handshake, dispatcher mapping, request validation and forbidden operations; include service handler integration.
5. **Audio end-to-end component path:** test `WindowsAudioSessionRuntime` start/stop/pause/resume, sender/receiver lane combinations, rollback, route update and device-loss behavior; use fake devices plus loopback UDP.
6. **Installer transactions:** automate clean install, upgrade, repair, rollback, uninstall, data/identity preservation, service recovery configuration, and installed ACL/prerequisite checks on disposable clean Windows images.
7. **Physical audio/session gate:** run and record WASAPI capture/playback under the intended service account across supported login/session/device scenarios before allowing an RC audio claim.

### P1 — Network, UI and failure recovery

1. Extend mocked `ControlCoordinator` tests for remote commands, capability/error/timeout rollback, desired-route recovery, network changes, queue bounds, and repeated lifecycle; add Windows adapter loopback tests for bind/send/receive/restart/fault recovery.
2. Add audio and Control Plane cross-device compatibility tests for Windows↔Android and ESP32 commands; maintain exact protocol fixtures.
3. Add ViewModel tests for snapshot/event ordering, stale epochs, reconnect/resync, command result/error flows, settings persistence and bounded metric history.
4. Add WinUI automation for service unavailable/retry, live IPC, stream commands, tray/remaining shortcuts, keyboard/focus/accessibility and UI-exit-during-stream continuity.
5. Exercise recovery matrices: endpoint removal, Control socket failure, IPC listener failure, Service crash and SCM recovery, config/log disk failure, installation/update interruption and rollback.

### P2 — Concurrency, stress, performance and soak

1. Add multi-threaded queue, route, event bus, Engine lifecycle and IPC client stress with explicit bounds and race invariants.
2. Add load profiles for audio packet rate, peer/discovery storms, malformed datagrams, max IPC clients, slow subscribers/readers and log bursts. Record CPU, allocations/GC, queue high-water marks, latency percentiles and drop/error counters.
3. Run at least the documented 8-hour RC soak; repeat overnight/24-hour stable qualification on reference hardware. Include visible/hidden/disconnected UI, simultaneous lanes, network interruptions and device hot-plug.
4. Add Android device soak under background/thermal/power constraints and ESP32 uptime/reconnect/queue-capacity tests on specified boards.
5. Preserve raw result artifacts with hardware, OS/SDK/firmware versions, app/package hashes, configuration, duration and acceptance decision. Do not infer hardware success from fake-backend tests.

## 6. Exit criteria for the roadmap

- Every production project has an explicit test owner and either automated contract coverage or a documented, repeatable manual hardware procedure.
- Protocol fixtures cover both valid and invalid boundaries and are exercised by every implementation that claims support.
- Runtime integration tests prove the Service/Engine/Control/Audio/IPC ownership and failure isolation together.
- Recovery and concurrency tests prove resources remain bounded and are released exactly once through partial start, disconnect, stop, crash and retry.
- UI tests cover authoritative-state reconciliation and prove UI termination does not stop Service-side work.
- RC/stable stress, soak, physical audio, LAN, and installer tests are recorded against the exact candidate binary set.

## 7. Repository references

- Release and hardware acceptance plan: [`TEST_PLAN.md`](/C:/Development/Python/AudioStreamer/docs/TEST_PLAN.md)
- Architecture constraints: [`ARCHITECTURE_RULES.md`](/C:/Development/Python/AudioStreamer/docs/ARCHITECTURE_RULES.md)
- Windows Service requirements: [`WINDOWS_SERVICE.md`](/C:/Development/Python/AudioStreamer/docs/WINDOWS_SERVICE.md)
- WinUI behavior and current test limits: [`UI_SPECIFICATION.md`](/C:/Development/Python/AudioStreamer/docs/UI_SPECIFICATION.md)
- Installer smoke script: [`Invoke-RcValidation.ps1`](/C:/Development/Python/AudioStreamer/tools/release/Invoke-RcValidation.ps1)
- CI workflow: [`windows-rc.yml`](/C:/Development/Python/AudioStreamer/.github/workflows/windows-rc.yml)

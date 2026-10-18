# AudioStreamer v2 Mandatory Architecture Rules

**Status:** Frozen and mandatory for all v2 implementation and review.
**Authority:** These rules supplement [Architecture v2](ARCHITECTURE_V2.md), [Engine Architecture](ENGINE_ARCHITECTURE.md), and the protocol specifications. Protocol documents remain authoritative for wire bytes and behavior.

## 1. Fixed technology and compatibility

1. Windows v2 uses .NET 9, C#, Windows App SDK, WinUI 3, Windows Service, Named Pipes, `System.Text.Json`, MVVM, dependency injection, and Windows audio APIs including WASAPI.
2. Do not introduce Rust, MAUI, Electron, Flutter, or replace the approved stack without a formal architecture revision.
3. Preserve Audio Protocol v3 and Control Protocol v1 wire compatibility. Protocol changes require coordinated specification, fixture, and implementation changes across all affected platforms.
4. Android remains an independent Kotlin implementation. ESP32 remains an independent C++ Control Plane controller and never carries PCM.

## 2. Ownership and boundaries

5. Windows Service is the machine-level source of truth and survives every UI process/connection lifecycle. The Engine it hosts is the authoritative owner of runtime node state, settings, peers, audio/control work, diagnostics, statistics, and internal events.
6. The Service owns SCM/process lifetime, service recovery, configuration/logging bootstrap, NodeHost composition, and Named Pipe host. The Engine owns node lifecycle and manager/driver orchestration through `IEngine` and injected ports.
7. WinUI owns views, view models, navigation, tray, notifications, dialogs, settings presentation, and the Named Pipe client only. The UI never owns networking, audio, discovery, peer registry, protocol codecs, machine configuration, or authoritative state.
8. UI close, tray Quit, process crash, forced termination, restart, pipe timeout, and heartbeat loss only disconnect that client. They must not call `Stop`, `Restart`, `StopNode`, `StopStream`, or otherwise mutate Engine/service state.
9. Explicit user commands such as Stop Stream or Stop Node are separate deliberate API calls. Installer/SCM stop is a separate privileged service lifecycle path.
10. Audio Plane carries PCM via Audio Protocol v3. Control Plane carries control messages via Control Protocol v1. Named Pipe IPC carries validated commands, snapshots, events, and bounded diagnostics only; it never carries PCM or raw LAN datagrams.

## 3. Dependency rules

11. Application/UI code depends on IPC DTO/client contracts, not Engine internals or platform drivers. Every Engine operation crosses the stable `IEngine` API; IPC may expose narrowly scoped host-only version/log/update status operations through explicit host services but never calls Engine managers.
12. Service/NodeHost composes `IEngine`, Engine managers, and platform drivers using explicit dependency injection. The Engine depends on abstractions and never references WinUI, SCM, Named Pipes, Windows-only APIs, installer code, or concrete platform drivers.
13. Platform drivers implement Engine-defined ports. Drivers know neither IPC nor UI and do not depend on each other. Networking code does not reference WinUI; manager code does not reference UI.
14. Subsystems do not call peer manager implementations directly. They communicate by narrow typed manager/coordinator ports or the immutable internal event bus. The event bus is not used as uncorrelated request/response RPC.
15. No circular project references, service locator pattern, hidden `IServiceProvider` resolution, or static mutable global state. Factories/scope factories are explicit injected dependencies.
16. Future compatible .NET hosts on Linux/macOS may reuse the platform-agnostic Engine and supply their own drivers. Android and ESP32 remain separate native implementations.

## 4. Lifetime, threading, memory, and errors

17. Every socket, audio device, worker, timer, queue, registry, settings writer, event subscription, and log sink has exactly one named lifecycle owner. Only that owner closes/disposes it.
18. Each class/module has one cohesive responsibility and a narrow documented interface. Avoid managers that accumulate unrelated responsibilities.
19. Thread/task ownership, cancellation source, queue bound, overflow behavior, ordering guarantee, and shutdown deadline must be documented for every asynchronous worker.
20. SCM/UI callbacks never block on audio/network cleanup. Shutdown is supervised asynchronously and awaited by the owning host.
21. Never allocate unbounded memory or block in audio hot paths. Audio callbacks may use only preallocated/reusable bounded buffers, atomic counters, and non-blocking signaling. No UDP, JSON, IPC, configuration, or ordinary logging runs on the real-time callback.
22. All queues, event streams, registries, buffers, IPC clients, retries, metric labels/history, and log retention are bounded with defined overflow/failure behavior.
23. Events and snapshots are immutable. No mutable collection, PCM buffer, pooled memory, socket buffer, or secret may escape its owner. Cross-layer settings and DTOs are validated and versioned.
24. Expected failures use typed results/errors with stable codes and safe messages. Exceptions, secrets, and protocol payloads never cross IPC or appear in logs. Cancellation and partial-start failure must release owned resources exactly once.

## 5. Configuration, diagnostics, logging, and updates

25. Engine ConfigurationManager owns canonical runtime settings, validation, and update revision. It persists through one injected storage adapter. Service owns only bootstrap/path/lifetime setup; UI-only preferences remain per-user.
26. Engine owns node/plane statistics and diagnostics projections. Service contributes SCM/process health through a host-health port. Logs are for operations, never the source of runtime state.
27. The Service initializes logging before starting Engine workers. The Engine may use the injected logging abstraction but never owns the host log file sink. No PCM, credentials, full IPC payload, or high-rate success records are logged.
28. UpdateManager is outside Engine and runs through a signed installer/updater boundary. The running Service/Engine never overwrites loaded binaries or accepts arbitrary update paths from UI.

## 6. Protocol and network safety

29. No ports, protocol constants, or endpoint defaults are duplicated as runtime magic literals; values come from versioned configuration/options and existing normative protocol contracts.
30. Audio and Control transports remain distinct, independently configured, and independently supervised. A failure in one plane must not implicitly tear down the other.
31. Control v1 is best-effort and unauthenticated on the LAN. Never report UDP send as peer acknowledgement or audible playback; retain the trusted-LAN limitation.
32. Named Pipe connections use local-only transport, restrictive ACLs, client-token authorization, length-bounded framing, schema validation, rate limits, and per-client bounded queues.

## 7. Review and change control

33. Any proposed exception requires an architecture decision record naming the affected rule, compatibility/security/lifecycle impact, migration plan, and owner approval before implementation.
34. Every change to ownership, interface, state machine, wire behavior, or event delivery updates the relevant architecture docs, repository map, tests/fixtures, and migration/release notes in the same change.
35. Reviewers reject code that violates these rules even if it passes functional tests. Architecture conformance is a release criterion.


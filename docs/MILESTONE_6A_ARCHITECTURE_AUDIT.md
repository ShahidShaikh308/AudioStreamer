# AudioStreamer V2 Milestone 6A — Architecture Conformance Audit

**Audit date:** 2026-10-13  
**Scope:** .NET v2 solution, service lifecycle, Engine, Windows audio adapter, Control Plane, IPC, WinUI client, deployment/configuration/logging scaffolding, and the mandatory architecture documents listed below.  
**Outcome:** **Not conformant for RC acceptance.** One lifecycle defect was corrected during the audit. Security and architecture-documentation findings remain open. Zero outstanding violations cannot be claimed.

## Documents reviewed

- `ARCHITECTURE_RULES.md`
- `ARCHITECTURE_V2.md`
- `ARCHITECTURE_AUDIT.md`
- `ENGINE_ARCHITECTURE.md`
- `WINDOWS_SERVICE.md`
- `IPC_PROTOCOL.md`
- `UI_SPECIFICATION.md`
- `INSTALLER_AND_UPDATES.md`
- `TEST_PLAN.md`
- `REPOSITORY_STRUCTURE_V2.md`
- `CODING_STANDARDS.md`

The source review covered every production `.csproj`, the solution-level build/package configuration, and the implementation files in `src/`. Existing test projects and their references were inspected. Runtime build and test execution could not be performed because the .NET SDK is not installed in this environment (`dotnet` is not recognized).

## Executive summary

The project-reference graph is acyclic and generally points in the intended direction. WinUI references UI.Core only; UI.Core references IPC and IPC.Contracts; IPC references IPC.Contracts; Engine has no project references; the Service composes Engine, Control.Protocol, IPC, Windows adapters, and Windows audio. The UI is an IPC client and does not reference Engine, Service, or audio drivers. Audio Protocol v3 and Control Protocol v1 remain separate implementations and the ESP32/Python/Kotlin paths are not coupled into the .NET solution.

The Engine is constructed as a singleton, owned by the Service host, and exposes `IEngine`. Windows audio resources are session-scoped under the audio runtime; worker queues and event/client queues have explicit bounds. Hosted-service registration order causes IPC to stop before NodeHost, preserving the Service-over-UI lifetime rule. Cancellation and asynchronous cleanup are generally propagated through owning workers, with accepted shutdown cleanup deliberately using non-cancelable tokens.

The principal confirmed defect was NodeHost stopping Engine resources before quiescing and stopping ControlRuntime workers. That order is now corrected. The IPC endpoint still falls short of the frozen security requirements: its DACL grants `AuthenticatedUserSid`, token/session identity is not checked against an intended interactive session, and mutation requests have no rate limiter. Those are outstanding conformance violations.

## Project and ownership map

| Project | References | Ownership/conformance observation |
|---|---|---|
| `AudioStreamer.Engine` | None | Platform-independent orchestration and contracts; no Windows, IPC, or UI reference. |
| `AudioStreamer.Control.Protocol` | None | Control Protocol v1 codec remains separate. |
| `AudioStreamer.IPC.Contracts` | None | Wire DTOs/contracts only. |
| `AudioStreamer.IPC` | IPC.Contracts | Owns Named Pipe framing, sessions, queues, handshake, and dispatch infrastructure. |
| `AudioStreamer.Audio.Transport` | Engine | Audio protocol/UDP components implement or consume Engine contracts; no cycle. This coupling should remain limited to adapter ports. |
| `AudioStreamer.Platform.Windows` | Engine | Windows configuration/platform adapter. |
| `AudioStreamer.Platform.Windows.Audio` | Engine, Audio.Transport | Implements Engine audio ports and composes Windows audio/UDP resources. |
| `AudioStreamer.Service` | Engine, Control.Protocol, IPC, IPC.Contracts, Windows adapters/audio | Composition root; owns SCM process, NodeHost, ControlRuntime, and IPC host. |
| `AudioStreamer.UI.Core` | IPC, IPC.Contracts | Client-side state/view-model logic; no Engine dependency. |
| `AudioStreamer.UI` | UI.Core | WinUI shell, navigation, tray, and application lifetime. |

No project-reference cycle was found. Composition uses DI rather than a general-purpose service locator. The Engine exposes internal seams to the Service and Windows audio adapter through `InternalsVisibleTo`; these seams are narrow in current use but should remain explicitly documented and should not expand into manager access.

## Findings

### A6-01 — NodeHost shutdown order allowed Control/Engine teardown races — **Fixed**

**Evidence:** `NodeHost.StopAsync` previously called `controlRuntime.Quiesce()`, then `engine.StopAsync()`, and only afterward `controlRuntime.StopAsync()`. Control receive, heartbeat, recovery, or remote-command workers could therefore touch an Engine whose audio/session/peer managers were already stopping.

**Decision:** Implementation defect. `WINDOWS_SERVICE.md` requires the Service to quiesce and stop Control Plane work before Engine resource teardown; this also preserves the Control Plane’s ability to send best-effort GOODBYE and finish route cleanup while Engine is still alive.

**Change:** `NodeHost.StopAsync` now quiesces ControlRuntime, awaits its stop, then awaits Engine stop. Stop cleanup remains owned by the Service and uses non-cancelable tokens once accepted.

### A6-02 — Named Pipe ACL is broader than the frozen authorization boundary — **Open, High**

**Evidence:** `WindowsPipeFactory.CreatePipeSecurity` grants `ReadWrite` to `WellKnownSidType.AuthenticatedUserSid`. `AuthenticateClient` checks authenticated/non-anonymous/non-guest status and records SID/admin membership, but does not validate the client’s Windows session against an intended interactive session.

**Decision:** Implementation defect against `IPC_PROTOCOL.md` §8 and `ARCHITECTURE_RULES.md` §32, which require the service SID, administrators, and intended interactive users, plus client-token/session validation. Local-only pipe flags reduce remote exposure but do not narrow which local authenticated principals may connect.

**Risk:** Another local authenticated account or service identity can reach the protocol. Per-operation authorization currently protects settings mutation, but the pipe ACL and identity/session policy are not least-privilege as specified.

**Required closure:** Define the supported multi-user/session policy, construct the DACL for those principals, and validate the impersonated client token/session before accepting a connection. Cover ordinary-user, administrator, other-session, and service-account cases in Windows integration tests.

### A6-03 — IPC mutation rate limiting is absent — **Open, High**

**Evidence:** `IpcServerOptions` bounds clients, queues, workers, and request duration, but defines no request-rate policy. `RequestDispatcher` dispatches mutations after envelope validation and timeout setup without a token bucket or equivalent per-client limiter. `RateLimited` exists in IPC error definitions but has no server-side enforcement path.

**Decision:** Implementation defect against `ARCHITECTURE_RULES.md` §32 and `IPC_PROTOCOL.md` §§8–9, which explicitly require mutation rate limits. Bounded concurrency does not impose a sustained request-rate limit.

**Risk:** A local client can repeatedly invoke stream/settings/discovery mutations, consuming Control Plane work and generating peer/network traffic despite bounded queues.

**Required closure:** Add a bounded per-client/identity mutation limiter with documented burst/refill behavior and a stable `RateLimited` response. Do not rate-limit read-only snapshot/diagnostic calls using the mutation bucket. Add deterministic tests for burst, refill, identity isolation, and queue interaction.

### A6-04 — Architecture documents disagree about Control Plane ownership — **Open, Documentation authority decision**

**Evidence:** `ARCHITECTURE_V2.md` §§2–3 and its component diagram place Control Plane policy/codec under Engine and state that Control Plane is inside Engine. `ENGINE_ARCHITECTURE.md` §10 and §14, plus `WINDOWS_SERVICE.md` “Milestone 4 implementation status,” describe a Service-owned `ControlRuntime` that owns Control v1 socket, timers, command correlation/recovery, and event publication while using `IEngine` and a narrow peer sink.

**Decision:** The implementation follows the detailed Engine/Service ownership description: `Program` registers Service `ControlRuntime`; `NodeHost` starts/stops it; it uses `IEngine` for node/session lifecycle and the narrow `IEngineControlPlaneSink` for peer observations. This is a documentation conflict, not a justification to move ControlRuntime into Engine during an audit.

**Risk:** Engineers can make incompatible changes depending on which frozen page they follow. The contradiction also affects whether `RestartDiscovery` and Control stream commands are Engine use cases or host Control Plane operations.

**Required closure:** An architecture owner must select the authoritative ownership rule and reconcile the conflicting pages, IPC operation mapping, Engine API contract, and repository structure before further Control Plane changes. No architecture redesign was made in this audit.

### A6-05 — IPC peer/control operations use a host facade instead of the documented IEngine mapping — **Open, Medium; coupled to A6-04**

**Evidence:** `EngineIpcRequestHandler` queries `IControlRuntime` for peers and Control statistics, calls `IControlRuntime.RestartDiscoveryAsync`, and sends `StartStream`/`StopStream` through `IControlRuntime`. The frozen `ARCHITECTURE_V2.md` says Engine operations map to `IEngine`; `ENGINE_ARCHITECTURE.md` describes Service ControlRuntime as the Control command owner and says routing is through the Engine registry and local Engine APIs.

**Decision:** The split is consistent with the detailed Service-owned ControlRuntime model for network negotiation, and `ControlRuntime` calls `IEngine` for local lane changes. The public `IEngine.GetPeersAsync` projection currently omits endpoint/capability/control metadata required by IPC, so the Service facade is also acting as a projection adapter over Engine-owned peer data. This is not a direct manager call from IPC, but the documents do not define this adapter contract clearly enough to certify it.

**Risk:** Future changes may create two sources of truth or accidentally expose mutable/internal peer records. `IEngine.RestartDiscoveryAsync` currently reports capability unavailable while the IPC operation succeeds through ControlRuntime.

**Required closure:** Resolve with A6-04. If the Service facade remains, document its host-only command/query contract and preserve the Engine PeerManager as the sole peer registry. If the frozen IEngine-only mapping is authoritative, the missing stable Engine contracts/use cases must be added through an approved API change rather than bypassing managers.

### A6-06 — Exception objects are written to console logs — **Open, Medium**

**Evidence:** Engine, ControlRuntime, IPC dispatcher/session/server, and event bridge call `LogError(exception, ...)`. The Service registers `AddJsonConsole` with scopes enabled. The rotating file sink records only exception type, but the console provider can serialize exception details/stack text; exception messages may include paths, endpoints, or arbitrary lower-layer details. Some IPC paths also log `exception.Message` as a `Detail` field.

**Decision:** Implementation defect against the structured, bounded, redacted logging requirement in `CODING_STANDARDS.md` and the safety rules in `ARCHITECTURE_RULES.md`.

**Risk:** Sensitive or unbounded lower-layer details may enter console/event collection. File-log redaction does not protect the independent console provider.

**Required closure:** Use a redacting exception policy consistently across all providers; preserve exception type, stable subsystem/error code, correlation IDs, and safe summaries. Avoid logging arbitrary exception messages. Add provider-level tests using sentinel secrets and long exception messages.

### A6-07 — Build and runtime conformance evidence unavailable — **Open, Release Gate**

**Evidence:** The environment has no `dotnet` executable, so compilation, unit tests, Windows Named Pipe ACL/session tests, service installation/recovery tests, and end-to-end audio/control LAN validation were not run.

**Decision:** Validation limitation, not evidence of source failure. The repository’s `TEST_PLAN.md` itself lists real LAN, audible switching, service deployment, fault injection, and long-duration testing as gates.

**Risk:** Static inspection cannot establish C# compilation, Windows API behavior, service recovery correctness, wire compatibility, or stream continuity under faults.

**Required closure:** Run the complete solution build/test matrix on a supported Windows .NET 9 runner, then execute the documented service/IPC/security/audio LAN acceptance tests and retain the results as release evidence.

## Cross-cutting audit observations

- **Dependency direction/cycles:** No cycle found. Engine has no project references. UI cannot reach Engine or platform drivers through project references.
- **Ownership/lifetimes:** Engine, ControlRuntime, IPC server, client sessions, audio sessions, and log sink each have explicit process/session owners. NodeHost shutdown ordering was corrected in A6-01. Service hosted registration puts IPC shutdown ahead of NodeHost shutdown.
- **Threading/cancellation:** Worker loops use cancellation tokens and bounded channels. Shutdown paths intentionally continue cleanup after acceptance. Review did not find synchronous waits in UI request paths; log-provider disposal does a bounded synchronous wait during provider disposal, outside audio/UI hot paths.
- **Audio/Control separation:** Audio.Protocol v3 code and Control.Protocol v1 code are separate. ControlRuntime does not consume PCM; IPC DTOs do not carry PCM. Audio transport includes per-packet/buffer allocations on asynchronous network paths; these are not WASAPI callback allocations, but allocation and GC impact remain a performance validation risk under sustained load.
- **UI lifecycle:** UI projects reference UI.Core; the coordinator owns reconnect state and app shutdown disposes the client. No UI close path was found that stops the service-owned Engine. The UI remains a client.
- **Protocol compatibility:** No wire-format changes were made. Compatibility and end-to-end interoperability still require execution of the protocol fixtures and cross-language tests on a supported environment.

## Changes made in this audit

1. Reordered `NodeHost.StopAsync` so ControlRuntime quiesces and stops before Engine teardown.
2. Added this audit report with the evidence, implementation/documentation decisions, risks, and closure requirements.

No audio, networking, IPC contract, Engine API, Service API, or protocol behavior was otherwise changed.

## Release decision

**Do not mark the architecture audit passed.** A6-02 and A6-03 are concrete IPC security violations; A6-04/A6-05 require an explicit authoritative architecture decision; A6-06 is a logging conformance gap; A6-07 blocks release evidence. The current state therefore does not meet the success criterion of zero remaining architecture violations.

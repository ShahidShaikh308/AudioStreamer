# AudioStreamer Architecture Audit

**Status:** Final v2 implementation-readiness review; documentation-only. No implementation source was changed.
**Scope:** Current repository architecture and documentation observed during v2 specification work.
**Severity:** P0 blocks production audio integration or release; P1 must be resolved before v2 release; P2 is planned debt/quality work.

## Executive summary

The repository contains v1 protocol and platform implementations. Windows currently runs as Python/Tk processes rather than an installed service-backed product. Existing abstractions separate parts of the runtime, and the Control Plane broker centralizes a Control UDP socket, but neither is the proposed Windows Service, WinUI client, nor Node IPC contract.

The frozen v2 blueprint specifies a platform-agnostic C#/.NET 9 Engine library hosted by a Windows Service, with a WinUI 3 client communicating over a versioned JSON Named Pipe protocol. The Service is the machine-level source of truth; the hosted Engine owns authoritative runtime node lifecycle, configuration, peers, statistics, diagnostics, and internal events. Android remains a native Kotlin implementation and ESP32 remains C++; both retain compatibility through normative wire documents and canonical fixtures. The Windows v2 runtime is not yet implemented or validated.

No v2 WinUI/C# project, Windows Service project, installer, or IPC implementation exists. Existing documentation describes current v1 facts and must not be mistaken for v2 implementation status.

## P0 — Production integration gates

### P0.1 Service-mode audio and Windows session model is unproven

Current capture uses PyAudioWPatch WASAPI loopback and playback uses PortAudio defaults from a normal desktop process. Moving those operations into a Windows Service changes process/session/account and endpoint-selection conditions. Loopback behavior alone does not prove current adapter selection or audible playback from the service context.

**Risk:** silent capture, wrong output, device access failure, or inconsistent behavior across logon/logoff/RDP.
**Required evidence:** a Windows service feasibility spike across clean boot, no user logged on, sign-in/out, fast user switching, endpoint change/hotplug, sleep/resume, and remote-audio scenarios. If a user-session helper is required, revise process and resource ownership explicitly while retaining one authoritative Service state model.

### P0.2 .NET Engine migration and protocol parity are unvalidated

Current Windows audio, Control Plane, node state, configuration, and broker are Python. Android and ESP32 have Kotlin and C++ implementations. The proposed Windows .NET Engine has not been ported or parity-checked.

**Risk:** protocol/state regressions, duplicated active runtimes, or migration scope underestimated.
**Required evidence:** .NET build/CI matrix, canonical byte fixtures, protocol positive/negative parity tests, audio/control lifecycle tests, and explicit retirement path for v1 Python Windows behavior. Keep old and new hosts from binding the same node identity or ports concurrently.

## P1 — Must resolve before v2 release

### P1.1 Existing local named pipe is not the v2 UI/service protocol

`control_plane/broker.py` uses `multiprocessing.connection.Listener` with a static key and Python framing to relay Control UDP datagrams. It is a helper subprocess topology; its purpose and ownership differ from the new UI/service JSON API.

**Risk:** confusing two unrelated pipe purposes, insecure key reuse, or retaining a second Control UDP owner after migration.
**Resolution:** Service owns the Windows network/node runtime; v2 uses the allow-listed, size-bounded JSON IPC endpoint. Treat the existing broker as v1 migration/reference code and remove it only after parity and rollback criteria pass.

### P1.2 Protocol behavior currently has multiple implementations

Audio Protocol v3 and Control Protocol v1 have Python, Kotlin, and C++ representations. The .NET Engine will add another relevant implementation. Normative documents define formats, but canonical cross-platform fixtures are not yet established in the current repository.

**Risk:** subtle serialization/acceptance differences, especially optional trailing transaction IDs and malformed input.
**Resolution:** commit shared positive and negative byte fixtures, run parity checks for each implementation, document active implementation ownership, and do not alter packet formats as part of the Engine port.

### P1.3 Configuration and port ownership are not yet one source of truth

Windows node configuration uses repository-root TOML; the control broker reads configuration separately; Device Manager preferences live in user AppData. Audio and Control ports appear in configuration and constructors.

**Risk:** node and broker disagree, upgrade loses identity/settings, or installed service depends on repository-relative paths.
**Resolution:** keep the Service process as the machine ownership boundary; Engine ConfigurationManager is the one runtime writer, with versioned migration, stable endpoint IDs, and validation through one injected store. Preserve existing migration values and remove duplicated runtime defaults during implementation. UI-only appearance preferences remain per-user.

### P1.4 Current Windows process topology violates v2 service authority

`main.py` creates the node and Tk UI in one process, and GUI close requests node shutdown. The separate Device Manager has its own Control Plane client.

**Risk:** UI exit interrupts streaming or multiple local processes create duplicate identities/policy state.
**Resolution:** v2 Service owns node resources and is the single source of truth. UI is an IPC-only client. Close, tray Quit, crash, and restart close only the UI connection; they do not stop or pause Engine, discovery, Control Plane, or active audio. Explicit stop actions remain deliberate, separately issued commands.

### P1.5 Installer and runtime assumptions are not implemented

Current Windows onboarding requires Python 3.13 and first-run dependency installation. V2 targets a .NET Engine/service and WinUI client, without Python as a stable product runtime.

**Risk:** installer downloads dependencies at runtime, deploys incompatible component versions, or retains an unintended Python requirement.
**Resolution:** bundle signed product components and supported .NET/Windows App SDK prerequisites. Keep Python packaging transitional/reference-only. Installer/update specifications define version matching, repair, rollback, and service lifecycle.

### P1.6 Installed-service security boundary needs implementation evidence

The current broker uses a static local authentication key; Control Protocol v1 is unauthenticated on the LAN. The installed service creates a stronger local security boundary than the current development process.

**Risk:** unauthorized local command/config access or unsafe update path.
**Resolution:** restrictive Named Pipe DACL, client-token authorization, service least privilege, frame/field/rate validation, redaction, signed updater artifacts, and a clearly documented trusted-LAN limitation.

### P1.7 Observability and target metrics need measured baselines

Current diagnostics expose some packet/queue/capture data, but there is no complete stable service health model or measured CPU/memory/startup/latency baseline. Audio Protocol v3 does not provide a timestamp for one-way latency measurement.

**Risk:** UI presents unavailable metrics or product claims lack a reproducible methodology.
**Resolution:** bounded typed diagnostics, explicit `Unavailable` for non-measurable latency, reference hardware/methodology, and performance/soak criteria in `TEST_PLAN.md`.

## P2 — Planned technical debt and scalability

### P2.1 Documentation currently describes both implementation and target architecture

README, BUILDING, DEVELOPMENT, Device Manager, Control Plane README, and architecture docs describe current Python/Tk/broker workflows. V2 specifications describe future components.

**Resolution:** retain current docs as v1/current-state until implementation; label v2 specs Proposed. At cutover, update user-facing guides and remove obsolete launcher instructions together.

### P2.2 Existing `core/` is not a shared cross-language runtime

Current `core/` contains Python contracts used by Windows and cannot be linked directly by Kotlin, C++, or .NET.

**Resolution:** treat it as v1 reference during migration. The .NET Engine is reusable by compatible .NET hosts, while Android and ESP32 remain independent native nodes governed by protocol contracts. Retire/rename v1 paths only after usages are removed.

### P2.3 Current transport and control acknowledgement limits remain

Current Control v1 requests are best-effort, and a sender has one configured audio destination. Multiple receiver selection does not create PCM fan-out. Audio UDP has no retransmission, encryption, clock synchronization, or congestion control.

**Resolution:** preserve these facts in UI/diagnostics and do not imply guaranteed delivery, fan-out, security, or measured one-way latency. Scope transport improvements separately and version them compatibly.

### P2.4 Product signing and release trust chain do not exist yet

No Windows installer or updater exists; the current README also does not select a project license.

**Resolution:** define signing, SBOM, installer rollback/repair, compatibility, release criteria, and license before public redistribution. Do not make the running Service overwrite its loaded binaries.

## 4. Current subsystem ownership findings

| Area | Current owner | Finding |
|---|---|---|
| Windows node | `WindowsAudioNode` in UI process | Lifecycle exists, but UI and node share process lifetime |
| Windows audio capture/playback | WASAPI/PortAudio adapters | Adapter boundary exists; service-session behavior unproven |
| Audio UDP | sender/receiver instances in node lanes | Separate from Control Plane; lane-scoped resources |
| Control UDP | Python Control Broker subprocess | One socket owner, but not a Windows Service |
| Windows control manager/registry | Each local ControlManager client | Registry/policy can be duplicated across local clients |
| Current local pipe | `multiprocessing.connection` broker transport | Control datagram bus, not the JSON UI/service contract |
| Windows UI | Tk node UI and separate Tk Device Manager | UI surfaces currently attach to different owners |
| Android | Kotlin `AndroidNode`/ControlManager and audio adapters | Native implementation; independent from Windows Engine |
| ESP32 | C++ Control worker/registry and OLED UI | Fixed-capacity, control-only implementation; no audio |
| Configuration | TOML node config and separate Device Manager preferences | No installed service-wide authoritative writer |

## 5. Architecture decision and document consistency

- Windows v2 uses a platform-agnostic C#/.NET 9 Engine, Windows Service/NodeHost, WinUI 3 client, JSON Named Pipe IPC, Engine-owned runtime configuration, diagnostics/statistics, and a Service-owned bootstrap/hosting boundary.
- The Windows Service starts and maintains Engine, discovery, Control Plane, and active audio independently of UI connections. UI close, Quit, crash, restart, or pipe timeout cannot issue `StopNode` or otherwise mutate Engine state.
- JSON is selected for Named Pipe IPC for explicit inspectable DTOs, .NET `System.Text.Json` support, schema evolution, and easier diagnostics. MessagePack remains a possible future optimization only if measured IPC cost justifies added schema/tooling complexity.
- Android stays Kotlin and ESP32 stays C++; protocol compatibility is proven with shared fixtures. Python remains migration/reference-only and is not a stable v2 runtime dependency.
- Root `README.md`, `BUILDING.md`, `DEVELOPMENT.md`, and `DEVICE_MANAGER.md` describe v1 today and must not imply v2 has shipped.
- Existing Windows audio defaults include destination port 5005 and local receive port 5006, while Control Plane default is 55050. Preserve these separate configured values during migration.

## 6. Audit disposition

No implementation source changes are part of this review. The technology stack, process ownership, public Engine boundary, event bus, DI/composition model, lifecycle, IPC encoding, and repository layout are frozen in the specification set. Service-session audio behavior remains a required feasibility spike before committing to the production audio host; .NET parity and security/interoperability validation remain release gates.

## 7. Implementation readiness disposition

### Critical before production audio integration

- Prove WASAPI capture, endpoint selection, and playback from the selected service identity/Session 0 across no-user, logon/logoff, device change, sleep/resume, and RDP cases. If not viable, stop and file a formal architecture revision before adding a UI-session helper.
- Implement the frozen `IEngine` API, event bus bounds, ownership rules, and Engine lifecycle semantics in a vertical slice. Verify service/UI disconnect has no Engine side effect.

### Medium implementation risks

- Port existing Python Windows policy/Control Plane/audio behavior into .NET and retain byte-for-byte v1/v3 compatibility.
- Migrate Node ID and supported machine settings once into Engine ConfigurationManager storage; test rollback/downgrade and unknown-field behavior.
- Implement Named Pipe ACL/token authorization and strict framing/schema validation before exposing mutation commands.
- Establish event coalescing and bounded channel capacities with measured slow-subscriber behavior; confirm state recovery always works through snapshots.
- Select target Windows versions, service identity, installer toolchain, and supported .NET/Windows App SDK deployment mode in release configuration without changing frozen boundaries.

### Optional improvements

- Add a developer-only event trace/replay tool using redacted event metadata, not PCM or production protocol changes.
- Add a separately versioned support-bundle exporter after core diagnostics and redaction rules are stable.

### Release blockers

- Service-mode audio feasibility and real-device/LAN interoperability are not demonstrated.
- Cross-language protocol fixture suite and .NET positive/negative parity are not implemented.
- Named Pipe security, signed installer/update chain, clean install/upgrade/rollback/uninstall, and recovery policy are unimplemented.
- Performance/soak claims remain targets only; no baseline measurements have been recorded.

### Risk register

| Risk class | Main exposure | Required control/evidence |
|---|---|---|
| Migration | Behavior drift moving Python Windows logic into .NET; duplicate active node during cutover | Fixture parity, staged migration, single port/Node ID owner, rollback rehearsal |
| Performance | Additional event/diagnostics work affecting audio throughput; service overhead | No hot-path event allocation/logging; aggregated counters; baseline CPU/RAM/latency/drop tests |
| Threading | Callback, UDP, manager, IPC, and SCM shutdown races | Explicit owner/cancellation scopes, bounded channels, supervisor shutdown tests, repeated stop/restart stress |
| Memory | PCM copies, slow IPC subscribers, peer/event accumulation, diagnostics retention | Reusable buffers, immutable metadata only, fixed capacities/coalescing, soak and saturation tests |
| Protocol | Different parser strictness, unreliable Control v1 commands, no Audio v3 ACK/timestamp/authentication | Normative docs/fixtures, truthful UI state, trusted LAN limitation, no unapproved wire changes |
| Packaging | Service/API runtime mismatch, service account/device behavior, failed updates | Signed version manifest, compatible atomic set, installer rollback, clean OS-image matrix and service audio proof |

### Freeze decision

**The architecture is frozen, and implementation may begin.** Begin with the service-audio feasibility spike and a minimal Engine/Service vertical slice that proves `IEngine`, DI ownership, event delivery bounds, IPC mapping, and UI-disconnect continuity. Passing those early gates does not remove the release blockers above; all release criteria remain required before product release.

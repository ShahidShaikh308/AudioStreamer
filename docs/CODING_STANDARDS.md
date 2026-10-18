# AudioStreamer Coding Standards

**Status:** Frozen v2 implementation standards. They apply when implementation begins; this specification does not impose new requirements retroactively on v1 files.

## 1. Architecture and ownership

- Assign exactly one owner to each socket, audio device, state machine, configuration source, timer, worker, queue, log sink, and persistent store.
- Preserve the dependency direction in `REPOSITORY_STRUCTURE_V2.md`: UI → IPC → Service/NodeHost → Engine and injected platform ports. The Engine must remain platform-agnostic and reusable by future .NET hosts on Linux/macOS.
- The Windows Service is the sole authority for node lifecycle, machine settings, peers, and runtime diagnostics. UI close, Quit, crash, and reconnect only affect its IPC client; they never stop/restart the Engine or send a runtime stop command.
- Use composition and explicit constructor injection. Avoid service locators, global mutable state, circular references, and inheritance-only extension.
- Commands request actions; immutable snapshots/events report state. Logs are never used as a state API.
- Keep Audio Plane and Control Plane separate. Audio Protocol v3 carries PCM; Control Protocol v1 carries commands/metadata. Named Pipe IPC is local UI/service control and diagnostics; it must never carry PCM.
- One implementation of a given wire protocol per language. Protocol documents and canonical fixtures are normative; do not add platform-specific wire variants.

## 2. Language and formatting

- **C#/.NET 9:** repository `.editorconfig` and `dotnet format`; nullable reference types enabled; async methods end in `Async`; public names use PascalCase; no `async void` except framework event handlers.
- **WinUI 3:** keep view code limited to presentation and framework integration. Put client behavior in testable view models/services. Avoid blocking the UI dispatcher.
- **Kotlin/Android:** ktlint-compatible style, immutable `val` by default, structured coroutine scopes, no `GlobalScope`.
- **C++/ESP32:** project formatter and warning-clean configured language standard; use fixed-capacity containers in timing-sensitive paths and avoid exceptions/dynamic allocation where firmware policy prohibits them.
- **Python v1 reference:** PEP 8, type hints on public interfaces, bounded queues; do not add v2-only behavior without corresponding migration/parity fixtures.
- Use descriptive names; abbreviations are limited to established terms such as UDP, IPC, PCM, WASAPI, and NodeId. Document non-obvious ownership, timing, security, and compatibility constraints.

## 3. Async work, real-time paths, and resource lifetime

- Do not block UI dispatcher or SCM service-control callbacks. Service callbacks signal the service supervisor and return promptly.
- Every worker belongs to a named owner, observes cancellation and completion, has bounded retry/backoff, and has a documented shutdown deadline.
- Do not create detached threads, timers, tasks, or coroutines. Fire-and-forget work is prohibited unless the owner tracks and observes completion and errors.
- Audio callbacks must not perform socket I/O, IPC, disk/configuration I/O, blocking locks, unbounded allocation, or ordinary logging.
- Keep buffers reusable and queues, registries, event backlogs, retries, client counts, and diagnostic history bounded. Define overflow behavior.
- Use monotonic time for deadlines, expiry, and elapsed durations. Use stable endpoint identifiers rather than transient enumeration indexes.
- Dispose OS handles exactly once in the component that created/owns them. Partial startup cleanup and stop paths must be idempotent.

## 4. Interfaces and data contracts

- Keep public subsystem interfaces narrow and capability-oriented. Inject platform ports into Engine composition; do not let the Engine discover or instantiate OS adapters.
- IPC DTOs are explicit, versioned, size-bounded, and independent of internal Engine classes. Validate every field and enum at the process boundary.
- Treat IPC acceptance, Control UDP send completion, remote acknowledgement, and audible playback as distinct outcomes.
- Audio/control packet formats are specified in `PROTOCOL.md` and `CONTROL_PROTOCOL.md`; IPC framing and compatibility are specified in `IPC_PROTOCOL.md`.
- Avoid implicit defaults at multiple layers. Machine-level values are read/validated/persisted by Service Configuration; user appearance preferences belong to the UI.

## 5. Error handling

- Return typed errors with stable subsystem/code identifiers and safe user-facing messages; preserve causal details in restricted diagnostics.
- Distinguish rejected command, accepted asynchronous operation, operation failure, degraded capability, and invariant failure.
- Recover within the owning subsystem where possible. A sender failure must not tear down receiver, discovery, Control Plane, or service IPC without a documented dependency.
- Never swallow errors or report successful shutdown while owned resources remain live. Rate-limit expected recurring network/device errors.
- Validate UDP bytes, IPC frames/JSON, persisted configuration, device IDs, and update metadata as untrusted input.

## 6. Diagnostics and logging

- Emit structured, bounded, redacted records with subsystem and correlation identifiers where useful. Service owns runtime logs; UI logs only client/presentation behavior.
- Never log PCM, credentials, signing material, full IPC bodies, or high-rate success messages.
- Metrics are bounded counters/gauges/aggregates. Report unavailable measurements explicitly rather than inventing values.
- Expose user-visible state through typed Service snapshots/events. Diagnostic records support investigation and do not replace state.

## 7. Documentation and protocol changes

- Update the owning component specification, lifecycle/interface diagrams, repository map, and test plan whenever an ownership or boundary changes.
- For a wire protocol change, update the normative document and compatibility matrix first, then coordinate all affected implementations and canonical fixtures. State version negotiation and failure behavior explicitly.
- IPC schema changes follow the independent compatibility rules in `IPC_PROTOCOL.md`.
- Public operations document cancellation, timeout, ownership, and failure behavior. Comments explain rationale and constraints rather than restating code.

## 8. Review checklist

- Does every resource and state machine have one explicit owner?
- Are dependency arrows one-way, with Engine free of platform/UI/SCM/IPC dependencies?
- Does UI lifecycle leave Service-owned discovery, Control Plane, and active audio unchanged?
- Are real-time operations allocation-light and free of blocking I/O/logging?
- Are queues, buffers, retries, timers, clients, and diagnostic retention bounded?
- Is IPC limited to validated control/state/diagnostics and free of PCM?
- Do protocol fixtures remain compatible across .NET, Kotlin, C++, and v1 reference implementations?
- Are ACLs, least privilege, input validation, redaction, and update-signature checks covered?
- Are shutdown/recovery and partial-start cleanup idempotent and observable?
- Do documentation, migration notes, release notes, and generated-file exclusions match the implementation?

## 9. Branch, review, and release policy

- Use short-lived `codex/<area>-<topic>` branches (or the explicitly approved project convention); do not commit directly to protected `main`.
- Submit focused reviewable changes with linked decision/issue context. Keep protocol redesign, UI polish, and installer work separate unless compatibility requires an atomic change.
- Use Conventional Commit prefixes (`feat`, `fix`, `docs`, `refactor`, `test`, `build`, `chore`). Never commit secrets, user config, signing keys, audio captures, generated outputs, or local logs.
- Require review from each affected owner (Engine, protocol, platform, security, release). Protocol and installer-signing changes need specialist review.
- Releases use semantic product versioning, signed artifacts, immutable release notes, compatibility matrix, test evidence, SBOM, and rollback package. A build is not stable until it meets `TEST_PLAN.md` stable criteria.

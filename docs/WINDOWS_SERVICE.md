# AudioStreamer Windows Service Specification

**Status:** Frozen v2 specification. The Service composes Engine with the Windows Control transport adapter, then starts IPC; Windows LAN/SCM integration remains to be verified.
**Owner:** Windows Service owns the Windows process and NodeHost lifetime.
**Related specifications:** [Engine](ENGINE_ARCHITECTURE.md), [IPC](IPC_PROTOCOL.md), [Installer and Updates](INSTALLER_AND_UPDATES.md).

## 1. Responsibility and process model

The Service process is the machine-level source of truth and the Windows SCM host for one node. It creates a `WindowsNodeHost` and .NET Engine instance with platform adapters. The default advertised node name is the Windows computer name; an existing custom node name remains in effect, while the legacy default `AudioStreamer Node` is migrated to the computer name. This process is the only installed Windows process allowed to host AudioStreamer audio/control UDP endpoints, capture/playback devices, peer registry, canonical settings, diagnostics, or active stream lanes. The Engine is the authoritative runtime owner for node state, settings, and diagnostics; the Service bootstraps and supervises it. WinUI is a replaceable local IPC client only; its connection count and lifetime never control Engine state.

The Service does not own views, navigation, tray UI, update download UX, or MSI transactions. It owns the IPC server, bootstrap configuration/path resolution, logging bootstrap, process health, and NodeHost/Engine DI scope. ConfigurationManager and DiagnosticsManager are Engine subsystems; durable storage and host-health data enter through injected ports. The service never bypasses `IEngine` to mutate node state.

## 2. Service lifecycle and startup order

SCM state machine:

```text
Stopped -> StartPending -> Running -> StopPending -> Stopped
                 |          |
                 +-> Failed <-+
```

Startup sequence:

1. Register SCM status handler immediately; report `StartPending` and checkpoint progress.
2. Load only bounded bootstrap configuration: storage/log paths, legacy configuration source, and safe logging options. Engine ConfigurationManager owns schema migration and stable Node ID loading. Current bootstrap migration failure writes a type-only diagnostic through `Trace` and exits nonzero before network/audio endpoints are opened.
3. Initialize structured JSON console and bounded JSONL file logging, process crash boundary, and health supervision. The rolling file sink is best-effort; pre-host startup has only the OS `Trace` fallback.
4. Build dependency injection with NodeHost, Windows drivers/factories, Engine managers/event bus, `IEngine`, configuration storage adapter, and IPC services. Engine ConfigurationManager loads/migrates and validates canonical settings before runtime resources start.
5. NodeHost invokes `IEngine.StartAsync`. Engine starts the Windows `IControlTransport` adapter and then its Control policy workers; the adapter binds the configured UDP endpoint and broadcasts are initiated by Engine policy.
6. After NodeHost completes, the separately registered Named Pipe host starts. The Service remains independent of UI availability.

ConfigurationManager validation/persistence failures are reported as repairable typed startup errors. Missing optional audio devices produce degraded capability state, not necessarily service failure. Control UDP bind failure is degraded or fatal according to policy; it must never silently share ownership. IPC server failure is isolated: Service continues Engine/audio/discovery work and independently retries/restarts its listener. It reports process health as degraded and accepts clients when available. IPC/UI failure must never terminate or restart Engine.

## 3. Running behavior and lifecycle commands

The service starts the Engine and discovery at service startup, whether or not a user is logged on or the UI has launched. It continues independently of UI visibility, connection, crash, restart, and process lifetime. UI launch negotiates IPC versions, subscribes, and requests a snapshot; it does not own or initialize the node. Window close hides to tray. Tray Quit disconnects and exits the UI without sending `StopNode`. This never pauses, stops, or restarts discovery, Control Plane communication, audio lanes, or service timers.

An explicit `StopNode` command drains active lanes and stops discovery; it is available only as a deliberate operation, never as a side effect of UI connection lifecycle. `StopService` is an SCM/installer action and follows the same drain procedure before process exit. `ShutdownService` is not a regular IPC command. Pausing is a separate Engine state and must have a precise effect; it does not mutate network protocol state. UI Quit is not StopNode or StopService.

## 4. Shutdown sequence

1. On SCM stop, enter `StopPending`, stop accepting new mutations, publish `ServiceStopping`, and signal the asynchronous NodeHost supervisor; SCM callback returns promptly.
2. IPC stops first because it is registered after NodeHost and host shutdown reverses registration order.
3. NodeHost calls `IEngine.StopAsync`. Engine quiesces Control command handling and sends best-effort GOODBYE through the transport.
4. Engine stops the Control policy workers and the Windows adapter, which closes the socket and awaits its send/receive tasks, then shuts down audio sessions.
5. Engine and Service scopes are disposed; process logging/bootstrap configuration is disposed last.
6. Report `Stopped` only after cleanup completes or an explicit timeout is recorded.

Repeated stop, stop during startup, and stop after a fatal error are idempotent state transitions. No service control callback performs blocking audio cleanup directly; it signals the supervisor and returns promptly.

## 5. Recovery and restart policy

SCM recovery actions are configured by the installer: restart after first and second unexpected failures with increasing delay; after repeated failures, leave the service failed and expose repair diagnostics instead of an infinite rapid loop. Reset the failure count only after a sustained healthy interval. A planned installer upgrade/stop must not be treated as a crash.

Engine Control policy retries transport recreation with bounded backoff after receive failure, expires peers using monotonic time, and re-advertises after the adapter reports a network-address change. Installed-Service recovery has not been verified. Service process restart creates a fresh Engine/runtime and begins discovery from its persisted Engine node ID; active streams are not resumed.

The UI reconnect policy is exponential backoff with jitter, bounded at a reasonable interval, and a fresh snapshot after every new connection epoch. UI failure has no service recovery effect.

## 6. Session and audio-device handling

Windows Service runs in Session 0 and cannot show UI or directly interact with an interactive desktop. Audio capture/playback behavior must be proven using the intended service account and supported Windows versions before adoption. Validate default endpoint selection, per-user audio preferences, endpoint hot-plug, user logoff, fast user switching, remote desktop redirection, sleep/resume, and no-user-logged-in startup.

Persist stable audio endpoint IDs, not device indexes. If the selected endpoint disappears, publish a device-unavailable event and transition only affected audio lane(s) to degraded/recovering. The service must not silently capture a different endpoint if that changes user intent; default-device-follow behavior must be an explicit setting. The UI enumerates devices through IPC and never calls WASAPI.

If a supported Windows configuration cannot meet the service audio requirements, the architecture must be revisited before implementation proceeds. Do not add an interactive helper process that owns audio without revising the one-owner contract.

## 7. Security

- Run under the least-privileged service identity that passes the audio/network proof. Do not use LocalSystem by default.
- Restrict install directory to administrators/service; mutable data/log directories have service write access and user read/export access as required.
- Use a named-pipe DACL for the service SID and intended interactive user groups. Validate the client token and authorize operations. Do not embed a shared secret in UI binaries.
- The service accepts only allow-listed commands and validated DTOs. It does not accept arbitrary process launch, file path, executable, or unrestricted destination endpoint requests.
- Restrict audio/control bind addresses and configured ports to valid ranges; Control Protocol v1 is unauthenticated on the LAN, so network operation is intended for trusted LANs.
- Do not expose diagnostic logs or settings containing local identifiers to unrelated user sessions. Multi-user policy is specified in the IPC document.
- Update/install operations require signed artifacts and elevated installer coordination, not privileged IPC methods callable by every UI client.

## 8. Configuration, diagnostics, and logging

Service bootstrap resolves machine-wide configuration storage and stable Node ID before Engine construction. Engine ConfigurationManager is the canonical runtime settings writer: it validates an immutable versioned snapshot and atomically persists updates through the injected `IConfigurationStore`. UI changes call `IEngine.UpdateSettingsAsync`; the Service/IPC layer does not write configuration directly. UI-only preferences remain per-user AppData.

Structured logs use rotation, size and retention limits, UTC timestamps, subsystem, severity, event ID, service instance ID, node ID, and correlation ID. No PCM, full pipe bodies, credentials, or high-frequency per-packet success records. Repeated errors are rate-limited.

Engine DiagnosticsManager exposes node/plane state, capability availability, peer count, lane states, bounded packet/drop/queue counters, device IDs, and last typed errors. Service contributes SCM/process health and CPU/memory host samples through a host-health port; estimates are labelled. Sampling is bounded (nominally once per second); diagnostics are not polled aggressively by UI.

## 9. Failure-mode behavior

| Failure | Service behavior | User-visible result |
|---|---|---|
| Invalid config | Do not open endpoints; report startup failure and repair hint | UI reports configuration needs repair |
| Control port already owned | Refuse duplicate ownership; report configured port and owner-independent troubleshooting | Control Plane unavailable, no false peer status |
| Audio endpoint missing | Keep service/Control Plane alive; mark relevant capability/lane degraded | Device unavailable with retry/selection action |
| UI process crash | Continue node operation | Relaunch reconnects and gets snapshot |
| IPC client malformed/oversized frame | Reject and close client; record bounded security event | Other clients and node unaffected |
| IPC server fatal fault | Keep Engine/streaming alive; isolate and restart IPC listener; mark health degraded | UI retries/reconnects and shows service management unavailable |
| Engine invariant failure | Stop Engine resources; report service fault and allow SCM recovery | Error state and diagnostic code |
| OS shutdown | Use SCM stop path and bounded drain; best-effort GOODBYE | No claim of guaranteed remote stop |
| Update failure | Preserve/restore previous compatible installation using installer rollback | Repair/update failure details |

## 10. Acceptance criteria

The service specification is satisfied when installed builds start automatically, run without a UI, keep streaming/discovery/Control Plane uninterrupted through UI close/Quit/crash/restart/pipe reconnect, recover independently from IPC listener failures, restore discovery and idle state after actual service recovery, stop each resource exactly once on real service stop, reject unauthorized IPC, and produce actionable diagnostics. Audio service/session testing is a release gate, not an assumption.

## Milestone 4 implementation status

The Service composes Engine, the Windows Control transport adapter, and IPC. Engine owns Control v1 discovery, heartbeat/expiry, peer policy, route negotiation, and command correlation. `WindowsControlTransport` owns the UDP socket, send/receive workers, and network-change notification. Engine session state owns platform runtime lifetimes. Audio sessions resolve peer audio endpoints from the Engine-owned registry, use one shared UDP receiver per local bind endpoint, and start sender/receiver lanes in negotiation-safe order. Peer timeout releases local audio resources and retains desired routes for renegotiated recovery after HELLO rediscovery. IPC reports Engine state and does not own Control policy. Automated mocked-transport tests cover discovery, heartbeat, peer expiry, routing, and shutdown; build/test execution, Windows Service deployment, socket fault injection, LAN recovery, long-duration operation, and audible switching remain unverified; do not treat the frozen acceptance checklist above as passed evidence.


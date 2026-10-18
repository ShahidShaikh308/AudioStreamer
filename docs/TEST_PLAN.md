# AudioStreamer v2 Test Plan

**Status:** Verification specification with initial UI Core navigation tests; execution status is recorded below.
**Scope:** Portable Engine, adapters, IPC, Windows product, cross-platform protocols, installer, and release qualification.

**Control Plane regression coverage added:** `AudioStreamer.Engine.Tests` now contains mocked-transport tests for HELLO discovery, heartbeat scheduling, peer expiry, correlated stream routing, and Engine shutdown. These source tests have not been executed in this environment because the .NET SDK is unavailable. UDP fault recovery, LAN interoperability, route recovery, soak, and Windows Service integration remain release validation work.

## 1. Verification principles

- A unit test proves a bounded software contract; it does not prove hardware, LAN, or audible behavior.
- Every protocol implementation is checked against normative byte fixtures and malformed-input rules.
- Tests use deterministic clocks, fake adapters, and fixed seeds where possible. Physical tests record OS/device/network versions and exact configuration.
- All queues, registries, IPC frames, logs, and retry loops are tested at their configured bounds.
- Failure injection is part of integration readiness: each owned resource is stopped exactly once and each state transition is observable.
- No release claim may be stronger than the test environment. “Build passed” is not “LAN interoperable.”

## 2. Unit and component tests

### Portable Engine

- Node aggregate, sender/receiver independent state transitions, duplicate/idempotent commands, invalid transition rejection.
- Control v1 and Audio v3 serializer/parser canonical vectors; byte-for-byte Python/Kotlin/ESP32 parity fixtures; boundary integers, payload limits, optional transaction IDs, UTF-8 limits, malformed lengths, reserved bits, invalid enum values, zero IDs, and truncated packets.
- Audio v3 sequence wrap/reorder/duplicate/stream restart behavior; stereo float32 frame alignment; packet split limits; duration-based queue bound and drop-oldest policy.
- Cross-language Protocol v3 exchange: send Python-serialized canonical and boundary datagrams into the .NET parser, then serialize in .NET and parse with the existing Python parser; compare both directions byte-for-byte against the shared `tests/ProtocolFixtures/audio_v3_canonical.hex` vector.
- Control discovery/heartbeat/GOODBYE, registry update/expiry, capability filtering, duplicate HELLO, source endpoint update, status handling, and command correlation.
- Monotonic clock behavior; deterministic heartbeat/expiry; cancellation and shutdown deadline races.
- Bounded memory/queue invariants and no mutable registry/snapshot aliasing.
- Engine assembly/API compatibility, invalid state transitions, malformed protocol inputs, cancellation/disposal behavior, bounded buffer ownership, and exception containment at subsystem boundaries.
- `IEngine` contract coverage: repeatable Start/Stop while scope lives, serialized Restart, cancellation before/after command acceptance, operation receipts, version reporting, atomic settings revision, and final-snapshot correctness.
- Internal event bus: per-subscriber order, monotonic sequence/revisions, immutable payloads, bounded slow-consumer behavior, statistics coalescing, critical overflow/resync, subscriber exception isolation/removal, and no PCM/raw datagrams.
- Manager graph and DI validation: no circular project references, no runtime service-locator resolution, one Engine instance per NodeHost scope, lane resource factories disposed by their owner, and no manager-to-manager implementation references.

### Adapters

- Fake capture/playback/datagram/store/clock adapters prove contract ownership and errors.
- Audio UDP tests cover v3 receive parsing, bounded queue overflow, sequence gaps/wrap, socket cancellation/rebind, invalid datagrams, and transport counters; send path is tested with loopback datagrams and a fake PCM converter.
- Adapter open/close idempotence, partial initialization cleanup, device removal, bind collision, send/receive errors, restart backoff, endpoint selection, format reporting, and bounded callback buffer exhaustion.
- Race checks for shutdown while callback/read/write is in flight; no use-after-dispose or resource use after owner shutdown.

### Service and IPC

- The initial IPC foundation suite covers bounded framing, fragmented reads, malformed JSON, frame-size rejection, handshake/version mismatch, request timeouts, client cancellation, concurrent requests, multiple clients, event subscription/unsubscription, disconnect/reconnect epochs, and service survival after a malformed client.
- JSON framing with fragmented/combined pipe reads, 0/maximum/over-maximum sizes, invalid UTF-8/JSON, required/optional fields, unknown versions, request correlation, event sequence/watermark, schema negotiation, timeout and cancellation.
- ACL/token authorization, role enforcement, concurrent clients, per-client queue limits, slow readers, client disconnect during response, reconnect epoch, service stopping, and resync-required behavior.
- Configuration migration, atomic save, corrupt file, invalid field, permission failure, stable Node ID retention, and rollback restore.
- Log redaction, rotation, retention, rate limiting, diagnostics bounds and sampling interval.

### UI

- View-model tests for snapshot/event reconciliation, gaps/resync, command pending/error states, capability filtering, stale peer indication, theme/reduced-motion preference, and no event polling loop.
- Accessibility automation for keyboard navigation, focus order, names/roles, high contrast, text scaling, and live-region announcements.

## 3. Integration tests

- Service + Engine + fake adapters: service starts Engine/discovery without UI, independent lanes, remote command routing, shutdown deadline, restart to idle after actual service recovery, and UI disconnect while streaming.
- Service composition order: bootstrap config/path resolution, logging, DI graph, Engine/manager readiness, IPC startup; injected failures at each phase release already-owned resources in reverse order and report through fallback diagnostics.
- Service + Windows adapters: real Control UDP discovery on loopback/test LAN, Control port exclusivity, audio UDP receive/send, device selection, device removal/recovery, and lane concurrency.
- UI + named pipe + service: clean attach, version negotiation, subscription before snapshot, watermark/event reconciliation without gaps, heartbeat, command/response, all UI exit paths during active streaming, reconnect without state mutation, multiple authorized clients, and service upgrade mismatch handling.
- UI close, Quit, crash, forced termination, and restart do not alter Engine state, discovery timers, Control UDP traffic, audio UDP, capture, or playback; compare counters/packet capture before, during, and after disconnect.
- IPC Heartbeat/HeartbeatAck loss closes only the pipe client after its timeout; Engine workers, peer registry, and streaming continue.
- Reconnection obtains a fresh snapshot/subscription and never replays an implicit start/stop/pause command.
- Node-to-node Windows ↔ Android and Windows ↔ Windows where available; confirm actual existing v1/v3 datagrams and both sender/receiver directions.
- Configuration migration from current `config/config.local.toml` and Device Manager preferences; preserve Node ID and supported ports/settings; report unsupported fields.
- Test that UI binaries contain no network/audio implementations or references to protocol serializers.

## 4. Hardware, LAN, and audio tests

Record machine model, CPU/RAM, Windows build, audio driver/device firmware, Android version, Wi-Fi/router/AP, network topology, configured ports, and test duration.

- WASAPI loopback from service account before login, after login/logout, fast user switching, RDP redirection, default-device change, device unplug/replug, sleep/resume, and protected/ineligible content behavior.
- Playback on the selected endpoint from service context, underflow recovery, sample-rate change, no-device behavior, and simultaneous capture/playback.
- Windows ↔ Android ↔ ESP32 discovery, heartbeat/expiry, sender/receiver selection, stream start/stop, reconnect, subnet/broadcast behavior, firewall block, Wi-Fi client isolation, VPN, and interface change.
- Listen for clipping, channel order, stutter, feedback, stale-buffer latency, and long-duration drift. Compare diagnostics with packet capture and audible result.
- Test 8-hour soak minimum for RC candidates; stable release requires repeated overnight/24-hour tests on representative hardware with no resource leak or stuck lane.

ESP32 tests remain firmware/board-specific: Wi-Fi reconnect, queue saturation, fixed registry capacity, display/input navigation, and command interoperability. No audio test applies to ESP32.

## 5. Performance and stress tests

Measure on documented baseline hardware with idle UI, hidden tray UI, one active lane, simultaneous lanes, and peer-heavy LAN. Collect p50/p95/p99, not a single best run.

Targets from product brief (release targets, not current facts):

| Metric | Target | Measurement rule |
|---|---:|---|
| Idle service RAM | 20–40 MB | Working set/private bytes after warm startup, idle discovery |
| UI + service RAM | 30–60 MB | Sum private bytes with UI open and idle |
| Idle CPU | <0.2% | 10-minute average after warmup; report sampling method |
| Streaming CPU | <2% | One sender + one receiver on reference device; define sample rate and endpoints |
| Service startup | <2 s | SCM start request to Engine idle; exclude boot time but report cold/warm |
| UI launch | <500 ms | Process launch to interactive Home using ready service |
| LAN audio latency | <50 ms | Define measurement method; v3 has no sender timestamp, so use an external loopback measurement and label limitations |

Stress scenarios: maximum configured peers, repeated discovery storms, malformed datagram flood, IPC clients at limit, slow IPC consumer, capture queue full, playback underflow, repeated device hot-plug, Control UDP bind collision, rapid start/stop commands, service restart during audio, disk full/log rotation, memory pressure, and OS sleep/resume. Verify bounded CPU/memory and no queue grows without limit.

## 6. Recovery and security tests

- Kill UI process during active stream: service and audio continue; UI relaunch resynchronizes.
- Terminate service unexpectedly: SCM recovery runs once per configured policy; Node returns idle/discovering, ID persists, old stream is not falsely reported active.
- Inject Engine crash, adapter hang, disk/config failure, full log volume, and hung shutdown worker; verify service reports accurate health and bounded termination.
- Attempt pipe access from unauthorized SID/session, malformed/oversized frames, unauthorized settings/log reads, command flood, and replayed stale connection epoch.
- Replace/tamper with package, manifest, binary, or version metadata; updater rejects before drain/install.
- Validate installed ACLs and that user-writable directories cannot replace service executable or Engine library.

## 7. Regression and compatibility

- Python v1 implementation remains an oracle during migration. Canonical fixtures are shared rather than generated independently from each implementation.
- Kotlin and ESP32 must continue to parse/respond to existing Control v1 messages; Windows Engine must continue to parse/send existing v1 semantics.
- Audio v3 packets must remain byte-identical and accepted by current Android receiver; test both directions where supported.
- Existing Node IDs, ports, receive/destination choices, names, and supported settings survive migration.
- No change to Android package/application ID or ESP32 capability declarations is implied by Windows v2.
- Explicitly compare golden fixtures and known limitations in README/protocol docs before every release.

## 8. Release checklist

- All unit/component/integration results are green on supported build agents and clean supported Windows images.
- Audio service-session proof passed for every supported Windows release/device class.
- Cross-device physical LAN checks pass; soak and stress results are attached to release record.
- Installer clean install, upgrade, rollback, repair, reboot/startup, uninstall, and config migration pass.
- Signatures, hashes, SBOM, version compatibility matrix, release notes, license notices, and support diagnostics are verified.
- No secrets, local config, logs, test captures with private audio, or development scripts are in release package.
- Performance targets are measured and either met or explicitly revised before stable release; no unsupported latency claim.
- Security review covers service account, pipe ACL, update chain, mutable paths, and Control Plane LAN exposure.

Milestone 4 updates the Engine session lifecycle and adds tests for deterministic session transitions, Audio v3 packet reordering/duplicate suppression, source-address receiver routing, missing packets, and playback underruns; existing queue tests cover bounded overflow. Control runtime now retains desired routes for peer recovery and renegotiates after rediscovery; Windows lanes roll back on startup failure. Remaining coverage includes Control negotiation and timeout/rejection scenarios, peer/service recovery integration, jitter adaptation and overflow edge cases, IPC event payload integration, and full socket fault injection. The .NET SDK is unavailable in this workspace, so all .NET compilation and test results remain unverified. Windows-to-Windows LAN audio, restart recovery, and long-duration behavior also remain unverified; do not claim milestone acceptance until these gates pass.

## 9. RC and stable criteria

**Release Candidate:** all P0/P1 defects closed; no protocol fixture mismatch; installer/rollback pass; service and IPC recovery pass; audio works on declared reference hardware; at least one cross-device LAN path passes in both directions; 8-hour soak passes; diagnostics and support workflow usable.

**Stable:** two or more RC cycles with no new P0/P1 defect; all supported Windows versions and declared audio device classes pass; Windows/Android/ESP32 compatibility matrix complete; repeated 24-hour soak passes; security review closed; stable performance baseline published; signed installer and rollback verified; release/support documentation complete. Hardware/LAN limitations remain plainly documented.

## 10. RC1 automation status

`.github/workflows/windows-rc.yml` is the initial automated Windows release gate. It restores and tests the .NET solution, runs the Python/.NET Audio v3 compatibility oracle, publishes service/UI payloads, builds the WiX MSI, then uses `tools/release/Invoke-RcValidation.ps1` on an elevated disposable runner to verify installation, automatic LocalService startup, recovery policy, forced-process recovery, named-pipe snapshot smoke, and JSONL log parsing. This does not substitute for the physical LAN/audio, real UI/service disconnection, clean-image prerequisite, upgrade/repair/rollback, security, or soak cases already listed above. Record those separately against the exact candidate package.

# Milestone 5B WinUI coverage

`tests/AudioStreamer.UI.Tests` currently verifies the UI-core navigation registry and stable titles. It does not exercise WinUI rendering, Mica/theme transitions, Global Search/keyboard accelerators, card selection/flyouts, diagnostics file picker, clipboard, tray behavior, or a live IPC connection. IPC handshake, request framing, and reconnect behavior continue to be covered at the IPC layer.

Required Windows 11/.NET 9 acceptance on a supported SDK host:

- Compile the full solution and run the UI Core tests; then launch the unpackaged app with and without the Service available.
- Verify Mica fallback, System/Light/Dark themes, window resize and 100–200% text scaling, keyboard navigation, focus visibility, high contrast, and screen reader names.
- Close, crash, and restart the UI during active streaming; verify the runtime continues uninterrupted and the new UI instance restores state from a fresh snapshot without replaying commands.
- Verify Ctrl+R, Ctrl+comma, Ctrl+L, device flyout route selection, stream start/stop, and reconnect retry. The global command search and Ctrl+K shortcut were removed.
- Verify peer/session empty states, disconnected view, unavailable metrics, and no missing statistic is rendered as zero.
- Exercise diagnostics copy/export and confirm only the redacted support summary is included; verify cancelled export has no side effects.
- Verify tray open/double-click, close-to-tray, start-minimized, Settings navigation, and Quit UI. Confirm the background runtime and streams remain untouched by UI exit.
- Run a sustained streaming soak with the UI visible and hidden; capture UI responsiveness, memory, CPU, and event update behavior.

No .NET SDK is available in this workspace, so build and test execution remain unverified. Toast delivery, audio endpoint management, detailed log viewing, and the corresponding acceptance cases are deferred because the current IPC/deployment contracts do not expose them.


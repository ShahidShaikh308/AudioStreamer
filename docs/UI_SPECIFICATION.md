# AudioStreamer Windows UI Specification

**Status:** Milestone 5B production UI pass; the Windows client remains an IPC-only frontend. Build and interactive acceptance are pending a .NET 9 Windows SDK host.
**Client:** WinUI 3, service-backed, named-pipe IPC only.
**Related:** [IPC Protocol](IPC_PROTOCOL.md), [Windows Service](WINDOWS_SERVICE.md).

## 1. Product boundary

The UI is a frontend and local remote control. The implemented WinUI 3 client owns navigation, temporary selection state, per-user theme preferences, tray behavior, and view models. It does not own node state, discovery, registry, sockets, capture/playback, protocol serialization, or persistent machine/node settings. The Windows Service is the single source of truth. UI view-model caches are read-only and disposable; authoritative data is requested via existing JSON Named Pipe IPC snapshots/events.

On launch, the UI attaches to the background AudioStreamer runtime, negotiates IPC versions through `PipeClient`, subscribes, requests a snapshot, then renders pushed events. Reconnection uses a fresh client, subscription, and snapshot. If unavailable, the UI offers Retry and reports automatic reconnection. It never controls SCM or falls back to local networking/audio. Closing and Quit UI only close the IPC client; the runtime and streams remain untouched. User-facing copy refers to AudioStreamer rather than exposing the Windows Service as an operational component.

### UI runtime state ownership

`IpcConnectionCoordinator` is the only component that owns and communicates through the IPC client. It submits the initial and reconnect snapshots and forwards incoming events to the singleton `AppStateStore`. `AppStateStore` owns the shared immutable `ServiceSnapshot` projection and applies peer, session, statistics, settings, and node-state events atomically. It uses event sequence and snapshot watermark values to ignore events already included by a snapshot and late snapshots that would overwrite newer events; a new connection epoch resets the sequence baseline after service restart. The active-session projection contains only `Running`, `Starting`, `Stopping`, and `Streaming`; `Negotiating`, `Preparing`, and `Recovering` are represented as `Starting`, while paused and terminal routes are omitted. Peer snapshots omit `Offline`, `Removed`, `Expired`, and `Unknown` entries. Observable view models project that store for binding; pages do not issue IPC requests, poll independently, or keep authoritative copies of peers, sessions, diagnostics, or statistics. Sender/receiver `ObservableCollection`s are incremental selector projections, not state owners. Transient selections, settings drafts, and bounded chart history remain view-local presentation state. Reconnect replaces the store snapshot before subsequent events are applied.

### Implemented capability boundary

Implemented screens: Home dashboard cards, virtualized Devices cards and peer detail flyout, Sessions cards and route controls, Audio Devices capability notice, live Statistics cards with in-session trend lines, Diagnostics support summary/export/copy, sectioned Settings, and About. Runtime values are sourced from IPC snapshots or events. Missing metric values remain `Unavailable`, never synthesized as zero. Statistics show aggregate reported throughput, queue depth, jitter target, latency average (only after samples exist), packets received, dropped packets, and recovered packets. The current server does not expose audio-device enumeration/selection, log paging, per-stream transport metrics, one-way LAN latency, toast notification commands, or service start/stop commands. These capabilities are clearly unavailable and are not represented as working controls. Node-name updates use the existing IPC update operation; theme, close-to-tray, and start-minimized are local UI preferences. Stream start/stop use existing runtime operations.

The tray is a WinForms notification-area shell around the WinUI window. Close-to-tray is a per-user preference; “Quit AudioStreamer UI” disconnects the app only. Stream menu actions are labeled as navigation to Sessions because they do not start or stop a stream directly.

## 2. Shell and navigation

One window with a persistent left navigation rail and a content frame. Do not create floating windows for normal pages. Navigation destinations:

1. **Home**
2. **Devices**
3. **Streaming**
4. **Audio Devices**
5. **Diagnostics**
6. **Settings**
7. **About**

The shell header shows app identity, service connection state, current node state, and a compact account-free tray/menu entry. Navigation selection is preserved per user. Page state is reconstructed from service snapshots after reconnect.

## 3. Screen specifications

### 3.1 Home

Show Node Name, Node ID (copyable), local IP addresses where known, service status, Engine version, Control Plane state, Audio Plane state, sender/receiver lane state, and current route when active. Status labels distinguish `Idle`, `Discovering`, `Streaming`, `Degraded`, `Service unavailable`, and `Needs attention`; never imply peer acknowledgement based solely on local UDP send.

Quick actions: Open Devices, Start/Stop selected route, Pause/Resume if supported. An explicit Stop Node action may be offered as a deliberate service command, separate from window/tray close. Disabled actions explain their precondition. A compact peer count and last actionable error are visible. Home is a summary, not a second configuration editor.

### 3.2 Devices

Display peers from service snapshot, keyed by stable Node ID. Columns/cards: display name, platform, address, capabilities, status, last-seen age, sender/receiver availability. Search and capability/status filters are UI-only. Peer detail shows the fields present in Control Protocol v1, source endpoint and capability flags; it does not provide direct peer messaging.

Selecting a peer may seed the Streaming page’s sender/receiver selectors. Stale peers are marked and removed according to service registry events; UI does not run a second registry or expiry timer.

### 3.3 Streaming

Present the route as Sender → Receiver → Connect/Stop. Sender and receiver selectors include only currently discovered peers advertising the required capabilities plus the local node when appropriate. Validate that IDs differ. Show the existing configured route, command request result, each local lane’s state, and returned STREAM_STATUS/ERROR when received.

The page explicitly distinguishes `Request sent`, `Remote status received`, and `Local audio active`; Control Protocol v1 has no guaranteed delivery or audible confirmation. Start/Stop are idempotent at UI level and disable while an operation is pending. Expose independent sender/receiver controls where existing node behavior supports them.

### 3.4 Audio Devices

Load endpoint lists using `GetAudioDevices`. Display friendly name, stable endpoint ID (secondary/copyable), input/output role, default marker, availability, and supported format information. Allow capture endpoint, playback endpoint, and follow-default selection. Apply through `SelectAudioDevices`; explain that a change may restart a lane and show resulting state.

The v2 UI must not promise unsupported sample-rate conversion, channel mapping, buffer-size, or latency controls. Show only settings the Engine and adapter can honor and report. If buffer size is exposed, describe it in milliseconds/frames with validated bounded choices and keep packet protocol limits intact.

### 3.5 Diagnostics

Show service/Engine/UI/IPC versions; uptime; CPU and memory samples; Control Plane and Audio Plane state; peer count; capture/playback device availability; packets and bytes; invalid/dropped packets; queue depth and buffered duration; capture drops; last error; and recent redacted logs. Values include units, sampling time, and `Unavailable` when not measurable. One-way LAN latency is unavailable under v3 unless measured by a separately defined method.

Diagnostics refreshes from pushed/coalesced events, with an explicit manual refresh. No aggressive polling. Log viewer is paginated and bounded; export action writes a redacted support bundle only after user selection. Never include PCM or credentials.

### 3.6 Settings

Sections:

- **General:** node display name (service-owned setting), launch UI at sign-in (UI only), close-to-tray, minimize behavior, notifications.
- **Discovery:** configured Control Plane port (advanced); discovery restart action; current status and troubleshooting.
- **Audio:** links to Audio Devices and supported buffer controls.
- **Appearance:** System/Light/Dark theme, reduced motion, UI language where translations exist.
- **Advanced:** log level within safe range, log retention visibility, diagnostics export, service version/repair status. No raw protocol editing.

Machine/node settings are submitted to the service and validated/persisted there. UI-only preferences are stored in per-user AppData. Opening the UI never starts/stops the Engine; the Service owns Engine startup. Unsaved changes show a confirmation dialog. Node ID and active protocol versions are read-only.

### 3.7 About

Show AudioStreamer product version, Engine version, IPC schema, Audio Protocol v3, Control Protocol v1, Node ID, license state/notice (until project license selected, show “License not yet selected” only in developer builds), credits, project/GitHub link if configured, privacy/support information, and update status/action. No claim of signed release or license should appear before those are established.

## 4. Tray, window, and service interactions

Closing the main window hides to tray by default and leaves the service and stream untouched. Tray menu:

- Open AudioStreamer
- Pause/Resume (if supported by current node state)
- Reconnect (reconnect IPC and resynchronize; does not restart LAN discovery)
- Settings
- Quit AudioStreamer (disconnect and exit UI; do not send StopNode or any stream/discovery command)

Minimize sends the window to taskbar or tray according to user preference. A single-instance UI forwards launch/activate to the existing process. Tray icon tooltip reflects the last service snapshot and marks it stale when disconnected. If service connection is lost, tray shows unavailable state and offers Retry/Open. IPC heartbeat detects a dead pipe client; missing heartbeats never cause service-side Engine shutdown.

Dialogs are reserved for destructive/ambiguous operations, permission/authorization errors, unsaved settings, repair, and update/restart. Routine stream transitions use inline status. Windows toast notifications are not enabled in the current unpackaged build; no notification preference is offered as if it were functional.

## 5. Accessibility and interaction

- All commands are keyboard reachable and have visible focus; navigation and peer/route controls have accessible names and roles.
- Do not communicate state using color alone; include text/icon and announce important changes via UI Automation live regions without flooding screen readers.
- Support text scaling, high contrast, system theme, and reduced motion.
- Animations are subtle, short, non-blocking, and disabled under reduced-motion preference. No animation is required to understand a state transition.
- Controls have predictable tab order, minimum target size, and error text associated with the input that caused it.
- Use localized resources for all user-facing strings; protocol names/IDs remain stable and are displayed with friendly labels.

## 6. UI state and error handling

UI connection states are `Connecting`, `Synchronizing`, `Subscribed`, `Connected`, `Reconnecting`, and `ServiceUnavailable`. Pages render immutable service snapshots; view models cannot mutate service state directly. Each command has pending/accepted/failed UI state keyed by request/operation ID. A lost response does not cause blind repeated stream starts; query snapshot before retrying a non-idempotent operation. The UI sends IPC heartbeats, but the Service does not use UI liveness as Engine or stream liveness.

Errors contain a concise explanation and next action. Raw exceptions, stack traces, pipe addresses, or internal paths are not shown. If schema/service versions are incompatible, show update/repair guidance and disable mutating actions.

The WinUI client keeps one immutable `AppStateStore` projection. `IpcConnectionCoordinator` is the sole pipe owner and applies subscribed events to that store. Snapshot synchronization uses the existing `NodeSnapshot.EventWatermark` and event sequence to discard already-included queued events and reject a snapshot response older than state already applied. View models and pages project that shared state; page notifications are scoped to the affected runtime category. Peer selector choices include capable `Online`, `Streaming`, `Busy`, `Receiving`, and `Sending` nodes. A settings update applies the `SettingsSnapshot` returned by the command, while remote `SettingsChanged` events trigger one targeted settings read instead of a full snapshot refresh. Identical settings snapshots and unchanged statistics counters do not trigger duplicate UI/chart updates.

The tray tooltip listens only to node-name and connection changes. Its recent-device menu is rebuilt only for peer events or a full snapshot, not for unrelated statistics and diagnostics notifications. Snapshot normalization reuses immutable peer and session arrays when they already satisfy the visible-peer and active-session rules.

## 7. Visual system and motion

Use native WinUI controls and platform conventions. Maintain consistent spacing, typography hierarchy, selected/hover/focus/disabled states, and compact data presentation. Avoid decorative dashboards that obscure operational state. Theme follows system by default. Motion communicates navigation/progress only, never substitutes for labels or causes blocking delays.

## 8. Acceptance criteria

The UI is acceptable when every displayed runtime value is sourced from IPC, close/Quit/crash/restart leaves service-side discovery, Control Plane, and active streams uninterrupted, reconnect resynchronizes from the authoritative service, all core operations work via keyboard and accessibility tooling, high-contrast/reduced-motion settings are honored, and diagnostics never require direct filesystem/network access by the UI.

## 9. Milestone 5B implementation and known limits

- WinUI shell uses Mica, Fluent controls, rounded cards, theme following, and local System/Light/Dark preference. Theme tokens and card templates are centralized in `App.xaml`.
- Dashboard, peer cards, session cards, statistics cards, and grouped settings are present. GridView peer cards use `ItemsWrapGrid`; views remain scrollable at narrow sizes.
- Ctrl+R refreshes the authoritative snapshot, Ctrl+, opens Settings, and Ctrl+L opens Diagnostics. The global “Search or run commands” control and its Ctrl+K shortcut were removed; page content expands into the available window width.
- Diagnostics export/copy includes a support summary only; it does not include logs, PCM, credentials, or arbitrary files.
- The UI receives session lifecycle events and aggregate counters, but IPC does not expose per-stream bitrate/latency/loss/recovery/queue fields. Charts show only server-reported aggregate counters and preserve missing readings as unavailable. The reported average latency is not a measurement of end-to-end one-way LAN latency.
- The current IPC event contract carries only aggregate status for diagnostics and only a settings revision for remote settings changes. The UI therefore updates diagnostics state from events and reads the latest settings after a settings-change event; it cannot receive full metric/settings deltas without an IPC contract change.
- Audio endpoint selection, live device-change handling, log filtering/search/collapse, modern toast delivery, update installation, tray direct stream commands, and complete high-contrast/localization/reduced-motion verification need additional platform contracts or installer/package work. They are not claimed as implemented in this milestone.
- The current UI project is unpackaged. Toast activation/identity prerequisites are not satisfied; adding toast infrastructure without deployment configuration would produce an unreliable product behavior.


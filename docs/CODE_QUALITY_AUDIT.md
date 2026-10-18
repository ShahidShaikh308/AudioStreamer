# AudioStreamer Code Quality Audit

**Date:** 2026-10-13  
**Phase:** Pre-refactor findings (implementation source unchanged when this report was written).  
**Scope:** Handwritten source and tests across the Python reference clients/planes, .NET 9 v2 solution, Android/Kotlin client, ESP32/C++ controller, and repository scripts. Generated `build`, `bin`, `obj`, `.gradle`, `reports`, virtual-environment, and cache content was excluded.

## Inventory and tools

The source inventory contains 220 source/script files: 103 C#, 52 Python, 20 Kotlin, 14 C++, 24 C/C++ headers, and 7 PowerShell/batch files. All source paths were included in the marker scan; subsystem review focused on production ownership, lifecycle, protocol, concurrency, I/O, and cleanup paths, and symbol-reference scans covered the .NET implementation and its adapters.

The available environment does not contain `dotnet`, Gradle/Java, or C++ analysis/build tools. Consequently this review could not use compiler warnings, IDE inspections, Android lint, PlatformIO, sanitizers, or executable test runs. The report separates source findings from runtime-verified claims; no behavior-equivalence claim is made solely from static inspection.

## Summary

- No handwritten `TODO`, `FIXME`, `HACK`, `XXX`, `WORKAROUND`, placeholder marker, or `NotImplementedException` was found. Matches for “temporary” were test helper names and atomic-save `.tmp` files, not unfinished implementation markers.
- No project-local circular dependency or obviously dead production class was established by reference inspection. The unavailable platform adapters are deliberate fallback/test compositions, and the cross-language protocol implementations are required for independent platform compatibility.
- Maintainability hotspots are concentrated in large coordinators (`ControlRuntime.cs` 653 lines, Android `AndroidNode.kt` 644, legacy Python Windows `node.py` 598, Windows GUI `gui.py` 442, Engine `Engine.cs` 332, and UI `Pages.cs` 330). Their size warrants focused follow-up, but extracting/repartitioning them without executable regression coverage is not a safe audit-only refactor.
- Several broad catches are intentional fault boundaries, but some native endpoint/notification cleanup catches silently erase failures. These are recorded for follow-up; changing diagnostics/error behavior is not behavior-neutral.
- The Python reference, Android, and ESP32 implementations intentionally repeat protocol-level logic across languages. No cross-language deduplication is appropriate. Within-platform repeated protocol constants must remain tied to the compatibility specifications/fixtures.

## Findings before refactoring

### CQ-01 — UDP sender and receiver stop is not retry-safe after caller cancellation — High

`AudioUdpSender.StopAsync` and `AudioUdpReceiver.StopAsync` atomically clear `_started` before awaiting the worker. If `WaitAsync(cancellationToken)` is canceled, the method exits before disposing/clearing its worker and lifetime source. A later `StopAsync` returns early because `_started` is already zero; `DisposeAsync` can therefore also skip waiting for the old worker. This creates an owner-lifetime leak on a canceled shutdown path.

**Safe action:** Preserve the one-time transition that completes/cancels work, but allow later stop/dispose calls to await any still-owned worker and finish cleanup. Normal stop behavior and packet behavior remain unchanged.

### CQ-02 — StateStore event wakeup counter grows for every projected event — Medium

`StateStore` constructs `_appliedSignal` as `new SemaphoreSlim(0, int.MaxValue)` and calls `Release()` for every event projection. Most events are already projected before `SynchronizeStateAsync` calls `WaitForSequenceAsync`, so their permits are never consumed. The count therefore grows monotonically and can eventually reach the maximum, where another event throws `SemaphoreFullException` and faults the projection worker.

**Safe action:** Treat the semaphore as a coalesced “projection may have advanced” signal with capacity one. `WaitForSequenceAsync` already rechecks the authoritative watermark in a loop, so coalescing wakeups preserves observable state and sequence semantics.

### CQ-03 — Diagnostics sampling accepts and discards statistics — Low

`DiagnosticsManager.SampleAsync` accepts a `StatisticsSnapshot` but does not use it (`_ = statistics`). Statistics are already stored and projected independently. The unused parameter obscures ownership and makes callers pass irrelevant data.

**Safe action:** Remove the unused internal parameter and update all call sites. No public API or snapshot content changes.

### CQ-04 — Naming convention is inconsistent in SessionsViewModel — Low

`SessionsViewModel` stores its logger in a lowercase `logger` field while neighboring view models and most C# fields use `_logger`.

**Safe action:** Rename the private field and its references only.

## Other maintainability and correctness observations

### Long coordinators and methods

- `ControlRuntime` combines socket lifetime, discovery, metrics, command negotiation, recovery, and peer event projection. This is cohesive as a runtime owner but large enough to raise review and fault-isolation cost. Do not split without a behavior-focused test suite.
- `AndroidNode` owns node lifecycle, capture/receiver worker coordination, UI state, permissions, and Control Plane event mapping. It has many synchronized state transitions and callback paths; future refactoring should be incremental and backed by Android lifecycle tests.
- Legacy Python `WindowsAudioNode` and `ControlManager` also combine policy and lifecycle. Those are reference implementations and should not be reorganized as part of this safe .NET cleanup.
- `EngineIpcRequestHandler.HandleAsync` is a long message-type switch. It remains an understandable protocol adapter; extraction could accidentally change validation/error mapping and is deferred.

### Exception handling and logging

- `WindowsAudioDeviceManager` intentionally tolerates Core Audio endpoints disappearing during enumeration, but `AddDefault`, `TryGetMixFormat`, `GetSupportedFormats`, and the notification worker contain empty catches. They can make persistent COM/API faults indistinguishable from transient device churn.
- Engine settings load/update and Windows audio session boundaries convert exceptions into safe typed results. These are intentional boundaries; avoid replacing them with broad UI-visible exception propagation.
- Python Windows playback and Control Manager isolate listener/device failures, but some paths log full exception traces and report `str(error)` through node events. This is useful operationally but should be checked against the project’s redaction expectations.
- Android `AudioPlayer.releaseTrack` uses `runCatching` for pause/flush/stop/release. It ensures cleanup proceeds, but discards the failure cause; cleanup diagnostics currently cannot say which release action failed.
- The .NET rotating file logger is bounded and its writer is isolated, but its `SelectFile` path enumerates/opens a file for every record. This is a performance/maintenance hotspot, not selected for behavior-sensitive rotation changes.

### Concurrency and lifecycle

- The .NET UDP sender and receiver cancellation issue is CQ-01. `WindowsAudioSessionRuntime.StopAsync` removes a lane from its registry before disposal; if disposal fails, it cannot retry through the registry. `DisposeAsync` also stops at the first lane-disposal failure, potentially skipping later lanes. These need an explicit failure/aggregation policy and focused Windows audio lifecycle tests before refactoring.
- `StateStore` wakeup accumulation is CQ-02. Its single-writer immutable projection and sequence recheck otherwise provide a clear synchronization model.
- The legacy Python broker creates daemon reader/writer/UDP threads and closes client connections without joining every worker. Shutdown can return while a thread is unwinding. This is legacy behavior and must not be changed without broker integration tests.
- The Python Windows node can synchronously call `ControlManager.update_local` from `_set_lane_locked` while its node lock is held; that call can perform transport work. This risks increasing lock hold time or lock-order coupling. Do not move it outside the lock without a transition/order regression test.
- Android state callbacks frequently enter synchronized sections and then publish StateFlow updates or log. The code needs Android thread/lifecycle tests before lock-scope changes.

### Allocation and real-time paths

- Audio Protocol v3 `.NET` parsing copies each datagram payload into a new array; sender packetization copies each payload into its packet object. These occur on UDP workers rather than native WASAPI callbacks, but can create sustained allocation/GC pressure under long streams.
- Python WASAPI callback converts each callback buffer to a new `bytes` and `PcmChunk`; the Android capture worker serializes into a reusable byte array, but its sender must consume/copy the callback payload before reuse. These are deliberate ownership/performance tradeoffs that require profiling before changing.
- C++ ESP32 control code uses fixed-size packet buffers, fixed peer storage, and bounded FreeRTOS queues; no heap allocation was found in the reviewed control-loop paths.

### Dead code, abstractions, and duplication

- `DiagnosticsManager.SampleAsync`’s statistics argument is the only confirmed dead parameter (CQ-03).
- Public Engine audio interfaces are wired through Windows adapter DI or reserved as platform ports; no safe dead-interface deletion was identified.
- `UnavailableAudioSessionRuntime` and `UnavailableAudioDeviceCatalog` are intentional fallback/test adapters, not production placeholders.
- Similar parser/serializer routines across Python, Kotlin, C++, and C# are separate language implementations of frozen wire protocols. They should remain separate implementations and be checked against shared fixtures, not “deduplicated” across platforms.
- Within the Python protocols, text validation helpers differ because Audio Protocol and Control Protocol have different field constraints; they are not copy/paste candidates.

## Safe refactoring plan

After this report, the audit will apply only CQ-01 through CQ-04. No protocol, public contract, routing, queue overflow policy, error message, audio buffer format, or UI behavior will be changed. The cleanup retry behavior in CQ-01 only completes disposal that was already requested; CQ-02 only coalesces redundant wakeups while preserving the watermark loop.

## Validation constraints

The repository’s available environment lacks the .NET SDK and Android/C++ toolchains. Refactoring will be checked by reviewing exact diffs and reference searches, but it cannot be declared compiled or runtime-tested here. Existing tests are not modified as part of the cleanup. A supported CI run remains necessary to confirm cross-language and Windows behavior.

## Refactoring completed after the pre-change report

1. UDP sender and receiver `StopAsync` now complete/cancel work only on the first stop transition but continue awaiting and cleaning up any retained worker on subsequent calls. This closes the canceled-wait retry leak without changing normal packet or queue behavior.
2. StateStore projection notifications now use a one-permit coalescing semaphore. The sequence-watermark loop remains the source of truth, so redundant event wakeups are collapsed without losing projected state.
3. Removed the unused statistics argument from the internal diagnostics sampling method and updated all call sites.
4. Normalized the `SessionsViewModel` logger field to `_logger`.

Static validation confirmed there are no remaining call sites using the removed diagnostics argument and no trailing whitespace in the modified source files. Compilation/tests could not be run because the required SDK/toolchains are unavailable.

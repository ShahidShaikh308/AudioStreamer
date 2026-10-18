# Engine RC1 Audit

**Scope:** Engine lifecycle, manager ownership, and conformance to `ENGINE_ARCHITECTURE.md`.
**Status:** Partial audit; this document does not certify the Engine or repository as release-ready.

## Finding fixed in this batch

`Engine.DisposeAsync` discarded the result of `StopAsync`. If a stream runtime refused
to stop, disposal continued and returned successfully, hiding an incomplete Engine
shutdown. Disposal now releases the event, state, and node infrastructure and then
throws an `InvalidOperationException` containing the typed shutdown error code when
`StopAsync` reports failure. A regression test exercises a runtime that starts a
session but refuses to stop it.

## Open Engine architecture issue

`ENGINE_ARCHITECTURE.md` defines `RestartAsync` as restarting runtime workers and
`RestartDiscoveryAsync` as controlling the Control Plane discovery worker. The current
Engine implementation changes NodeManager lifecycle state without a worker restart;
`RestartDiscoveryAsync` returns `CapabilityUnavailable`. The Windows IPC request
handler instead invokes `ControlRuntime.RestartDiscoveryAsync` directly. This leaves
the frozen Engine boundary and Service dispatch behavior inconsistent. Resolving it
requires a coordinated Engine/Service ownership decision and is not changed in this
batch.

## Verification

The regression test was added but could not be executed because the .NET SDK is not
available in this environment. Compilation and runtime validation remain required.

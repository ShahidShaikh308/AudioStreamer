# Contributing

Before making changes, read [ARCHITECTURE.md](ARCHITECTURE.md), [PROTOCOL.md](PROTOCOL.md), [CONTROL_PROTOCOL.md](CONTROL_PROTOCOL.md), and [DEVELOPMENT.md](DEVELOPMENT.md). Keep the Audio Plane (PCM transport) independent from Control Plane orchestration and platform UI.

Do not change a wire format without updating its normative protocol document and every implementation together. Keep platform-specific APIs behind platform adapters. Avoid blocking work on UI threads, unbounded queues, and claims that compilation proves hardware behavior.

For a change, run the relevant platform build and Python compile/import checks in [BUILDING.md](BUILDING.md). In the change description, state what was validated and what still requires hardware, LAN, or listening verification. Do not commit credentials, local configs, signing keys, build output, or logs.

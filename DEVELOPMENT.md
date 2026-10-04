# Development Guide

## Environment

### Windows Python application

- Windows 10/11, Python 3.13 (64-bit), and Tkinter.
- Dependencies are pinned in `platforms/windows/requirements.txt`.
- `AudioStreamer.bat` is the supported user launcher. It creates the root `.venv`, installs dependencies, and starts `main.py`.

For development, create/use the same virtual environment and install the pinned requirements. Run the desktop entry point from the repository root with `.venv\\Scripts\\python.exe main.py`. Do not start individual capture or networking modules as application entry points; `platforms/windows/desktop/bootstrap.py` composes platform implementations and `platforms/windows/desktop/node.py` owns independent sender and receiver lifecycles. The GUI depends only on the `INode` API in `core/node/interfaces.py`.

### Android application

Open `platforms/android/` in Android Studio, or use its Gradle wrapper:

```powershell
cd platforms/android
.\gradlew.bat :app:assembleDebug
```

The output APK is `platforms/android/app/build/outputs/apk/debug/app-debug.apk`. Keep the established `applicationId` `com.audiostreamer.receiver`; changing it would make Android treat the refactored app as a different installed application. Kotlin source packages may be reorganized independently from that install identity.

## Validate changes

There is no single device-independent command that proves end-to-end audio quality. For source-level work:

1. Review `git diff --check` for whitespace or patch errors.
2. Compile Python modules with the project interpreter and import the Windows entry point.
3. Build Android with `:app:assembleDebug`.
4. For runtime changes, capture and play a real signal on Windows and Android hardware; check both application diagnostics and logs. A localhost synthetic-PCM check validates packet delivery and playback setup but does not replace a system-audio capture or Wi-Fi test.

Do not claim successful real-time playback from a compile-only result. Device routing, Windows endpoint formats, Wi-Fi loss, phone audio policy, and scheduling affect runtime behavior.

## Coding principles

- Keep Audio Plane modules independent of UI, platform capture/playback APIs, discovery, and routing.
- Keep platform-specific device access under `platforms/<platform>/`.
- Keep protocol wire definitions and normative behavior in `PROTOCOL.md`. Never change protocol v3 field order, meaning, sizes, or validation while retaining version 3.
- Keep serialization/parsing separate from socket lifecycle and application orchestration.
- Pass typed PCM chunks/formats across module boundaries; do not make the transport depend on a particular capture implementation.
- Keep the Control Plane command/status-only. It may request lifecycle actions; it must not carry audio samples.
- Prefer one responsibility per module and explicit resource ownership/cleanup.
- Keep platform implementations injected at a composition root; user interfaces and future control components depend on node contracts, not concrete engines.
- Publish node status as immutable events/snapshots. Marshal cross-thread events to UI event loops before updating UI state.
- Avoid premature abstractions: add extension points when a real second implementation needs them.
- Preserve existing packet compatibility unless a separately documented protocol version is deliberately adopted.

## Configuration and logs

The Windows application owns `config/config.toml` and `logs/audiostreamer.log`. Keep runtime-generated files out of source control. Android persists its node name, destination IP/port, receive port, and log level in app-private `SharedPreferences`; configure peer destination ports to match the receiving node's configured receive port.

## Change workflow

Before editing, identify the owning plane and platform. Update the protocol specification and both implementations together only when wire behavior intentionally changes. For architecture-only changes, verify imports and build paths and state clearly which runtime checks were or were not performed.

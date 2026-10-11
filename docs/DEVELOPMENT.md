# Development Guide

## Environments

### Windows

Use 64-bit Python 3.13 with Tkinter. Dependencies are pinned in `platforms/windows/requirements.txt`. `AudioStreamer.bat` prepares the root `.venv` and launches the audio node. `AudioStreamerDeviceManager.bat` launches the standalone control client after the environment has been prepared. The Windows composition root is `platforms/windows/desktop/bootstrap.py`; the UI depends on the node contract rather than capture or socket internals.

### Android

Open `platforms/android/` in Android Studio or use the Gradle wrapper from that directory:

```powershell
.\gradlew.bat :app:assembleDebug
```

The debug APK is `app/build/outputs/apk/debug/app-debug.apk`. Keep the existing `applicationId` `com.audiostreamer.receiver` to preserve install identity. See [BUILDING.md](BUILDING.md) for SDK/JDK requirements.

### ESP32

Open `esp32_controller/` as a PlatformIO project. Copy the ignored Wi-Fi settings template to `include/audiostreamer/config/WifiSettings.h` and set local credentials. Do not commit the local file.

## Validation

Use the commands in [BUILDING.md](BUILDING.md). Python syntax/import validation and Android/ESP32 compilation do not prove LAN discovery, capture policy, hardware input, or audible output. Record hardware and cross-device validation separately.

## Architecture and coding principles

- Keep the Audio Plane focused on PCM transport and playback; keep Control Plane logic focused on discovery and commands.
- Keep Audio Protocol v3 and Control Protocol v1 wire definitions in their normative root documents. Update every implementation together only for an explicitly approved protocol change.
- Keep serialization separate from socket ownership and keep OS-specific device APIs behind platform adapters.
- Use bounded queues and explicit ownership for sockets, audio engines, worker threads, and coroutines.
- Keep UI code on UI threads and move socket/audio work to background workers. Marshal immutable events/snapshots back to the UI.
- Prefer a small implementation with clear ownership over abstractions that have no second use yet.

## Runtime data

Windows creates `config/config.local.toml` on first launch, using the defaults represented by `config/config.example.toml`. It stores a persistent random nonzero 64-bit Node ID and the Control Plane port (default `55050`). Android stores its equivalent in app-private `SharedPreferences`. The Device Manager stores local preferences under the user's application data directory. ESP32 Wi-Fi credentials stay in its ignored `WifiSettings.h`. Logs, runtime config, credentials, and keys must not be published.

## Control Plane ownership

On Windows, `control_plane/broker.py` owns the sole Control Plane UDP socket. Local Windows managers exchange datagrams with the broker over an authenticated named pipe. The broker binds without UDP address reuse so a second owner cannot silently share the port. Android's `ControlManager` owns its socket in a private `Dispatchers.IO` coroutine and queues outbound packets through a bounded channel. The ESP32 transport and its background FreeRTOS worker own its socket. Do not add socket operations to an Activity or Tk callback.

Android's node constructs the Audio Plane `UdpSender` on IO and hands PCM from the MediaProjection callback to a bounded queue; the worker copies the callback-owned bytes and sends them from IO. The MediaProjection prompt and foreground-service lifecycle stay on Main because Android requires them there. Closing the Android Control Manager queues GOODBYE and returns; its IO worker drains queued messages and closes the socket.

## Protocol and extension workflow

For a new Control Protocol message or field, document its exact bytes, validation, compatibility behavior, and version implications in [CONTROL_PROTOCOL.md](CONTROL_PROTOCOL.md), then update Python, Kotlin, and firmware implementations in the same change. Keep Control messages free of PCM. For Audio Protocol changes, follow [PROTOCOL.md](PROTOCOL.md) and update all audio parsers/serializers together. Add cross-language byte fixtures before claiming interoperability.

## Adding a platform or UI screen

First decide whether it is an audio-capable node or a control-only client. Put platform APIs under `platforms/<platform>/` or the standalone `esp32_controller/` project, and wire them at the platform composition root. UI screens should render and translate input to typed operations; they must not open sockets, access audio devices, or own peer registries. On ESP32, preserve the background control worker and bounded screen data.

## Review checklist

- Run `git diff --check`.
- Run Python compile/import checks and the relevant platform build.
- Review generated outputs and ensure secrets/local settings remain ignored.
- State whether validation was compile-only, localhost, LAN, or physical-hardware testing.
- Update README or protocol/architecture docs when behavior, requirements, or limitations change.

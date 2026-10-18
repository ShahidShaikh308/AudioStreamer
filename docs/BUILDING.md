# Building AudioStreamer

Build each platform separately. These commands compile artifacts; they do not prove that devices can discover one another or that playback sounds correct.

## .NET Windows v2 RC pipeline

The Windows v2 solution targets .NET 9 and is built by `.github/workflows/windows-rc.yml` on Windows Server 2022. The solution is Release-only: an unqualified `dotnet build` or `dotnet test` uses Release, and project builds explicitly selecting Debug fail rather than producing a second configuration. Locally, install the .NET SDK with the .NET 9 targeting packs, then run `dotnet build AudioStreamer.sln` and `dotnet test AudioStreamer.sln`. The release workflow also runs Python/.NET Audio Protocol v3 compatibility checks, publishes Service and unpackaged WinUI payloads, builds the x64 WiX MSI, and exercises install, IPC snapshot, logs, SCM recovery configuration, and forced-process recovery on a disposable elevated runner. Run `tools/release/Invoke-RcValidation.ps1` only on a disposable elevated Windows machine; it installs and uninstalls the MSI.

This is a validation scaffold, not a signed release process. The workflow currently emits an unsigned CI MSI artifact. Production signing, SBOM generation, clean-image prerequisite validation, upgrade/repair/rollback, and physical audio/LAN/soak tests remain release gates.

## Windows node and Device Manager

Requirements: 64-bit Windows, Python 3.13 with Tkinter, and an internet connection on first launch. From the repository root, run `AudioStreamer.bat`. The launcher creates `.venv`, installs the pinned dependencies in `platforms/windows/requirements.txt`, and launches the node UI. After that environment exists, run `AudioStreamerDeviceManager.bat` for the control-only UI. The node configuration is created at `config/config.local.toml`; the safe template is `config/config.example.toml`.

For syntax/import validation without starting audio devices:

```powershell
python -m compileall -q audio_plane control_plane control_client core platforms main.py
python -c "import main, control_client.main"
```

The WASAPI capture and PortAudio playback adapters require Windows hardware/runtime validation.

## Android

Requirements: Android Studio or Android SDK 37, JDK compatible with the checked-in Gradle wrapper, and Android Gradle dependencies. From `platforms/android`:

```powershell
.\gradlew.bat :app:assembleDebug
```

The debug APK is written to `app/build/outputs/apk/debug/app-debug.apk`. Install it using Android Studio or `adb install -r app/build/outputs/apk/debug/app-debug.apk`. Android 10/API 29+ and user-approved MediaProjection are required to capture mixed playback; receiving/playback has a lower minimum API.

## ESP32 controller

Requirements: PlatformIO Core, an ESP32 DevKit V1, and the documented OLED/joystick hardware. Copy `include/audiostreamer/config/WifiSettings.example.h` to the ignored `include/audiostreamer/config/WifiSettings.h` and set local Wi-Fi credentials. From `esp32_controller`:

```powershell
python -m platformio run
python -m platformio run --target upload
```

The project pins the ESP32 platform and Adafruit display dependencies in `platformio.ini`. Hardware behavior must be verified on the target board.

## Cross-device validation

Use devices on the same reachable IPv4 LAN. Permit the configured Control Plane UDP port (default 55050) and audio UDP ports through host firewalls. Wi-Fi client isolation, VPNs, and guest networks commonly block broadcast or peer-to-peer UDP. Validate discovery, both stream directions, simultaneous send/receive, clean shutdown, and long-running behavior on physical devices before making runtime claims.

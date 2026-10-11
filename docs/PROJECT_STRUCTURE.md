# Project structure

```text
audio_plane/                 Shared PCM packet model, protocol v3, UDP, playback buffering
control_plane/               Python Control Protocol v1, peer registry, Windows broker/client transport
control_client/               Standalone Windows control-only Device Manager
core/                        Platform-neutral audio and node contracts
platforms/
  windows/                    WASAPI capture, Windows playback, node composition and Tk UI
  android/                    Kotlin node, protocol/audio transport, capture and AudioTrack playback
esp32_controller/             Standalone PlatformIO control-only hardware controller
config/                       Safe Windows config example; runtime config is local and ignored
docs/                         Architecture and node design notes
PROTOCOL.md                    Audio Protocol v3 contract
CONTROL_PROTOCOL.md           Control Protocol v1 contract
```

The Audio Plane carries PCM. The Control Plane performs discovery and requests lane changes. They are separate protocols and responsibilities. Windows and Android implement audio-capable nodes; the Windows Device Manager and ESP32 are control clients. The ESP32 has no audio implementation. `platforms/android/app/src/main/java/com/audiostreamer/control_plane/` contains the Kotlin Control Protocol codec, peer registry, and coroutine-owned UDP manager.

Generated outputs such as Python bytecode, Gradle build directories, PlatformIO `.pio`, logs, runtime configuration, and Wi-Fi settings are excluded from source control.

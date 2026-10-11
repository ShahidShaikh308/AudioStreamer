# ESP32 Controller

The active ESP32 project is the standalone `esp32_controller/` PlatformIO Arduino project for ESP32 DevKit V1 (ESP-WROOM-32). It implements Wi-Fi station connectivity, persistent Node ID, Control Protocol v1 discovery/heartbeats/GOODBYE, a bounded peer registry, and an OLED/joystick UI for sender/receiver selection and stream commands.

It is a Control Plane client only. It never captures, transports, buffers, or plays PCM. The OLED uses SSD1306 at I²C address `0x3C` (SDA 21, SCL 22); the KY-023 uses X GPIO34, Y GPIO35, and button GPIO32. Copy the example Wi-Fi settings header to the ignored `include/audiostreamer/config/WifiSettings.h` before building or flashing. See the project README for PlatformIO commands and current limitations.

The controller emits the existing Control Protocol v1 message types. Its UI can show local command state but cannot confirm peer acceptance or audible playback. Validate on hardware and a reachable LAN.

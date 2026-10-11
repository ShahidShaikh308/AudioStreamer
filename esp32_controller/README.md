# AudioStreamer ESP32 Controller

Standalone PlatformIO firmware for the ESP32 DevKit V1 (ESP-WROOM-32), using Arduino and C++17. The controller is a Control Plane client with an SSD1306 display and KY-023 joystick. It does not capture, receive, or transmit audio.

## Current capabilities

- Persists a 64-bit node identity in ESP32 Preferences.
- Connects to Wi-Fi in station mode and retries after disconnects.
- Announces itself with Control Protocol v1 `HELLO` and `HEARTBEAT`, answers discovered peers, handles `GOODBYE`, and expires stale peers.
- Maintains a bounded registry of 16 peers and presents sender/receiver lists based on advertised capability bits.
- Queues existing `START_STREAM` and `STOP_STREAM` messages through the Control Plane worker and displays returned stream status/errors.
- Provides Boot, Connecting, Discovering, Main Menu, Sender Selection, Receiver Selection, Confirm, Streaming, and Settings screens.

Audio capture and transport, discovery changes, and new Control Protocol message types are outside this firmware's scope. It uses the existing Control Protocol v1 message layout; no protocol behavior is added here.

## Architecture and ownership

```text
KY-023 -> Ky023Joystick -> ControllerApp -> ControllerUi -> SSD1306
                              |                 |
                              |                 +-- screen/menu rendering
                              +-- polls Wi-Fi and node snapshots
                              |
                              +-- IControlPlaneClient command queue
                                      |
                               Control Plane worker
                               owns UDP transport + peer registry
                                      |
                               ControlProtocolV1 codec
```

The Arduino main task polls input, Wi-Fi reconnect status, and the latest Control Plane snapshot, then renders at a throttled rate. A dedicated FreeRTOS task owns all Control Plane UDP operations and the peer registry. FreeRTOS queues transfer commands and immutable snapshots between the main task and worker. Wi-Fi event callbacks only report connection state. No network operation is performed by a screen.

The main polling path uses fixed-capacity arrays and stack frames; it does not allocate dynamically. Driver/library initialization may allocate internally during startup (for example, the SSD1306 framebuffer and FreeRTOS queues). The Control Plane task reserves an 8 KiB stack for its packet buffers and nested parsing/snapshot work. The registry retains at most 31 bytes of a node name and 15 bytes of a platform string for local display/storage; incoming protocol text is parsed according to its wire length before this bounded copy.

## Hardware

| Device/signal | Connection |
| --- | --- |
| SSD1306 128×64 OLED | I²C address `0x3C`; SDA GPIO 21; SCL GPIO 22 |
| KY-023 X axis | ADC GPIO 34 |
| KY-023 Y axis | ADC GPIO 35 |
| KY-023 button | GPIO 32, active-low with internal pull-up |

The joystick uses 12-bit ADC reads, configurable center/dead zone and button debounce. Direction changes and button press/release are edge events; held directions do not repeat.

## Build and flash

Install Python 3 and PlatformIO Core, or use the PlatformIO IDE extension. From this directory:

```sh
python -m pip install platformio
python -m platformio run
python -m platformio run --target upload
python -m platformio device monitor --baud 115200
```

The build does not require Wi-Fi credentials. To enable Wi-Fi, copy `include/audiostreamer/config/WifiSettings.example.h` to `include/audiostreamer/config/WifiSettings.h`, then replace the example values:

```sh
cp include/audiostreamer/config/WifiSettings.example.h include/audiostreamer/config/WifiSettings.h
```

The private file is ignored by Git. Keep credentials out of source control.

## Repository layout

```text
platformio.ini
include/audiostreamer/
  app/             Controller composition
  config/          Controller configuration and credential example
  control_plane/   v1 codec, peer registry, queues, and worker contract
  display/         Display frame and display interfaces/driver contract
  input/           Input events and joystick interface/driver contract
  network/         Wi-Fi and UDP Control Plane transport interfaces/drivers
  ui/              UI model, screen, menu, and UI interfaces
src/
  app/             Main-task orchestration
  config/          Persistent node identity
  control_plane/   Protocol, registry, queue, and worker implementation
  display/         SSD1306 driver
  input/           KY-023 driver
  network/         Wi-Fi and UDP implementations
  ui/              Screens, menu, and UI manager
  main.cpp         Arduino entry point
```

## Validation and limitations

Build validation uses `python -m platformio run`. A successful host-side PlatformIO build verifies compilation and linking, not hardware operation. Wi-Fi, OLED, joystick, and peer interoperability require flashing and testing on the target board/network. The normative v1 contract is the repository-root [`CONTROL_PROTOCOL.md`](../CONTROL_PROTOCOL.md).

There is no audio functionality in the ESP32 controller. It is a control-only node. The registry is fixed at 16 peers; names are shortened for local storage/display. The UI can queue start/stop requests but does not route audio itself.

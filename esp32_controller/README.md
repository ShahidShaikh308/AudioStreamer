# AudioStreamer ESP32 Controller

Standalone PlatformIO firmware for the ESP32 DevKit V1 (ESP-WROOM-32), using Arduino and C++17. The controller is a Control Plane client with an SSD1306 display and KY-023 joystick. It does not capture, receive, or transmit audio.

## Current capabilities

- Persists a 64-bit node identity in ESP32 Preferences.
- Connects to Wi-Fi in station mode and retries after disconnects.
- Passively listens for other nodes' Control Protocol v1 `HELLO`, `HEARTBEAT`, and `GOODBYE` messages and expires stale peers. It does not announce itself, answer peer announcements, or send a `GOODBYE`, so it does not appear in the audio nodes' peer lists.
- Maintains a bounded registry of 16 peers and presents sender/receiver lists based on advertised capability bits.
- Sends authenticated Control Protocol v2 `START_STREAM` and `STOP_STREAM` commands through the Control Plane worker and displays returned stream status/errors.
- Opens directly to a compact status dashboard after Wi-Fi connects. It shows network/control-plane readiness and the live node count.
- Provides a guided New audio route flow that filters senders and receivers by capability, excludes selecting the same node twice, and asks for a final confirmation before sending commands.
- Provides a nearby-device list with each device's platform, online state, IP/port, and send/receive capabilities, plus an Active routes view that tracks multiple simultaneous routes.
- Settings are read-only on the controller and show current Wi-Fi/control readiness, local IP, discovered node count, and configured ports; configuration remains in firmware settings.

The ESP32 remains a passive observer: it consumes unauthenticated v1 discovery and status messages but never announces itself or joins the nodes' peer lists. Controller commands use v2 HMAC-SHA256 authentication. V1 node-to-node streaming remains unchanged. The shared-key setup and receiver compatibility rules are documented in [`CONTROL_PROTOCOL.md`](../docs/CONTROL_PROTOCOL.md).

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
                               listens on UDP + owns peer registry
                                      |
                               v1 discovery/status + v2 HMAC command codec
```

The Arduino main task polls input, Wi-Fi reconnect status, and the latest Control Plane snapshot, then renders at a throttled rate. A dedicated FreeRTOS task owns all Control Plane UDP operations and the peer registry. It receives node announcements and heartbeats but sends no presence traffic. FreeRTOS queues transfer commands and immutable snapshots between the main task and worker. Wi-Fi event callbacks only report connection state. No network operation is performed by a screen.

The main polling path uses fixed-capacity arrays and stack frames; it does not allocate dynamically. Driver/library initialization may allocate internally during startup (for example, the SSD1306 framebuffer and FreeRTOS queues). The Control Plane task reserves an 8 KiB stack for its packet buffers and nested parsing/snapshot work. The registry retains at most 31 bytes of a node name and 15 bytes of a platform string for local display/storage; incoming protocol text is parsed according to its wire length before this bounded copy.

## Hardware

| Device/signal | Connection |
| --- | --- |
| SSD1306 128×64 OLED | I²C address `0x3C`; SDA GPIO 21; SCL GPIO 22 |
| KY-023 X axis | ADC GPIO 34 |
| KY-023 Y axis | ADC GPIO 35 |
| KY-023 button | GPIO 32, active-low with internal pull-up |

The joystick uses 12-bit ADC reads, configurable center/dead zone and button debounce. Direction changes and button press/release are edge events; held directions do not repeat. In the UI, Up/Down moves through lists, the button selects or confirms, and Left returns to the previous screen. The guided route flow is: New audio route → sender → receiver → confirm. From the dashboard, Nearby devices opens status/details and Active routes lists concurrent routes. Select a route and press the button to request that route stop; Left returns to the list. Routes are removed when a stop or failure is observed, or when either peer sends GOODBYE or expires from the peer registry. Commands remain best-effort and the controller cannot confirm that audio has stopped at the receiving device.

## Build and flash

Install Python 3 and PlatformIO Core, or use the PlatformIO IDE extension. From this directory:

```sh
python -m pip install platformio
python -m platformio run
python -m platformio run --target upload
python -m platformio device monitor --baud 115200
```

The build does not require Wi-Fi credentials. To enable Wi-Fi and controller commands, copy `include/audiostreamer/config/WifiSettings.example.h` to `include/audiostreamer/config/WifiSettings.h`, then set the Wi-Fi credentials and the same 64-hex-character controller secret used by the Windows and Android nodes:

```sh
cp include/audiostreamer/config/WifiSettings.example.h include/audiostreamer/config/WifiSettings.h
```

Set `AUDIOSTREAMER_CONTROLLER_SECRET_HEX` in that private header before compiling and flashing. Leave it empty to disable controller commands. The private file is ignored by Git. Keep both Wi-Fi credentials and the controller secret out of source control.

## Repository layout

```text
platformio.ini
include/audiostreamer/
  app/             Controller composition
  config/          Controller configuration and credential example
  control_plane/   v1 discovery codec, v2 authenticated commands, peer registry, queues, and worker contract
  display/         Display frame and display interfaces/driver contract
  input/           Input events and joystick interface/driver contract
  network/         Wi-Fi and UDP Control Plane transport interfaces/drivers
  ui/              UI model, screen, menu, and UI interfaces
src/
  app/             Main-task orchestration
  config/          Persistent node identity
  control_plane/   Protocol v1/v2, registry, queue, and worker implementation
  display/         SSD1306 driver
  input/           KY-023 driver
  network/         Wi-Fi and UDP implementations
  ui/              Screens, menu, and UI manager
  main.cpp         Arduino entry point
```

## Validation and limitations

Build validation uses `python -m platformio run`. A successful host-side PlatformIO build verifies compilation and linking, not hardware operation. Wi-Fi, OLED, joystick, and peer interoperability require flashing and testing on the target board/network. The normative v1 contract is the repository-root [`CONTROL_PROTOCOL.md`](../CONTROL_PROTOCOL.md).

There is no audio functionality in the ESP32 controller. It is a control-only node. The registry is fixed at 16 peers; names are shortened for local storage/display. The UI can queue start/stop requests but does not route audio itself.

#pragma once

#include <cstdint>

namespace audiostreamer::config {

struct ControllerConfig final {
    const char* nodeName = "AudioStreamer ESP32 Controller";
    std::uint16_t controlPort = 55050;
    std::uint16_t receivePort = 5006;
    std::uint16_t joystickCenter = 2048;
    std::uint16_t joystickDeadZone = 450;
    std::uint16_t joystickDebounceMs = 35;
    std::uint16_t wifiReconnectIntervalMs = 5000;

    static constexpr std::uint8_t kDisplaySdaPin = 21;
    static constexpr std::uint8_t kDisplaySclPin = 22;
    static constexpr std::uint8_t kJoystickXPin = 34;
    static constexpr std::uint8_t kJoystickYPin = 35;
    static constexpr std::uint8_t kJoystickButtonPin = 32;
};

}  // namespace audiostreamer::config

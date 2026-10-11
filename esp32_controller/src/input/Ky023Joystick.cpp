#include "audiostreamer/input/Ky023Joystick.h"

#include <Arduino.h>

#include <cstdlib>

namespace audiostreamer::input {

Ky023Joystick::Ky023Joystick(const config::ControllerConfig& config) : config_(config) {}

bool Ky023Joystick::begin() {
    analogReadResolution(12);
    pinMode(config::ControllerConfig::kJoystickXPin, INPUT);
    pinMode(config::ControllerConfig::kJoystickYPin, INPUT);
    pinMode(config::ControllerConfig::kJoystickButtonPin, INPUT_PULLUP);

    rawButtonPressed_ = digitalRead(config::ControllerConfig::kJoystickButtonPin) == LOW;
    stableButtonPressed_ = rawButtonPressed_;
    buttonChangedAtMs_ = millis();
    previousDirection_ = readDirection();
    return true;
}

Ky023Joystick::Direction Ky023Joystick::readDirection() const {
    const int x = analogRead(config::ControllerConfig::kJoystickXPin);
    const int y = analogRead(config::ControllerConfig::kJoystickYPin);
    const int center = config_.joystickCenter;
    const int xOffset = x - center;
    const int yOffset = y - center;
    const int absoluteX = std::abs(xOffset);
    const int absoluteY = std::abs(yOffset);
    const int deadZone = config_.joystickDeadZone;

    if (absoluteX < deadZone && absoluteY < deadZone) return Direction::Neutral;
    if (absoluteY >= absoluteX) return yOffset < 0 ? Direction::Up : Direction::Down;
    return xOffset < 0 ? Direction::Left : Direction::Right;
}

void Ky023Joystick::emitDirectionEdge(Direction direction, InputState& state) const {
    switch (direction) {
        case Direction::Up: state.up = true; break;
        case Direction::Down: state.down = true; break;
        case Direction::Left: state.left = true; break;
        case Direction::Right: state.right = true; break;
        case Direction::Neutral: break;
    }
}

InputState Ky023Joystick::poll() {
    InputState state{};
    const Direction direction = readDirection();
    if (direction != previousDirection_) {
        emitDirectionEdge(direction, state);
        previousDirection_ = direction;
    }

    const std::uint32_t now = millis();
    const bool buttonPressed = digitalRead(config::ControllerConfig::kJoystickButtonPin) == LOW;
    if (buttonPressed != rawButtonPressed_) {
        rawButtonPressed_ = buttonPressed;
        buttonChangedAtMs_ = now;
    }

    if (rawButtonPressed_ != stableButtonPressed_ &&
        static_cast<std::uint32_t>(now - buttonChangedAtMs_) >= config_.joystickDebounceMs) {
        stableButtonPressed_ = rawButtonPressed_;
        state.pressed = stableButtonPressed_;
        state.released = !stableButtonPressed_;
    }

    return state;
}

}  // namespace audiostreamer::input

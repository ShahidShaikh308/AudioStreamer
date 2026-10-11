#pragma once

#include "audiostreamer/config/ControllerConfig.h"
#include "audiostreamer/input/IInput.h"

namespace audiostreamer::input {

class Ky023Joystick final : public IInput {
public:
    explicit Ky023Joystick(const config::ControllerConfig& config);
    Ky023Joystick(const Ky023Joystick&) = delete;
    Ky023Joystick& operator=(const Ky023Joystick&) = delete;
    Ky023Joystick(Ky023Joystick&&) = delete;
    Ky023Joystick& operator=(Ky023Joystick&&) = delete;

    bool begin() override;
    InputState poll() override;

private:
    enum class Direction : std::uint8_t {
        Neutral,
        Up,
        Down,
        Left,
        Right,
    };

    Direction readDirection() const;
    void emitDirectionEdge(Direction direction, InputState& state) const;

    const config::ControllerConfig& config_;
    Direction previousDirection_ = Direction::Neutral;
    bool rawButtonPressed_ = false;
    bool stableButtonPressed_ = false;
    std::uint32_t buttonChangedAtMs_ = 0;
};

}  // namespace audiostreamer::input

#pragma once

#include <cstdint>

namespace audiostreamer::input {

enum class InputEvent : std::uint8_t { None, Up, Down, Left, Right, Pressed, Released };

struct InputState final {
    // Each flag is true for one poll only when its corresponding edge occurs.
    bool up = false;
    bool down = false;
    bool left = false;
    bool right = false;
    bool pressed = false;
    bool released = false;
};

class IInput {
public:
    virtual ~IInput() = default;

    virtual bool begin() = 0;
    virtual InputState poll() = 0;
};

}  // namespace audiostreamer::input

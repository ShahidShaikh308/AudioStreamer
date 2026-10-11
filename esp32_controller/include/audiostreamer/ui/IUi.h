#pragma once

#include <cstdint>

#include "audiostreamer/input/IInput.h"

namespace audiostreamer::display {
class IDisplay;
}

namespace audiostreamer::ui {

class IUi {
public:
    virtual ~IUi() = default;

    virtual bool initialize() = 0;
    virtual void handleInput(input::InputEvent event) = 0;
    virtual void render(display::IDisplay& display) = 0;
    virtual void updateStatus(bool wifiConnected, std::uint8_t peerCount) = 0;
};

}  // namespace audiostreamer::ui

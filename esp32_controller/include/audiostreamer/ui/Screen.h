#pragma once

#include <cstdint>

#include "audiostreamer/display/IDisplay.h"
#include "audiostreamer/input/IInput.h"

namespace audiostreamer::ui {

enum class ScreenId : std::uint8_t {
    Boot,
    Connecting,
    Discovering,
    MainMenu,
    SenderSelection,
    ReceiverSelection,
    Confirm,
    Streaming,
    ActiveRoutes,
    Settings,
    Devices,
    DeviceDetails,
};

class Screen {
public:
    virtual ~Screen() = default;
    virtual void enter() = 0;
    virtual void leave() = 0;
    virtual ScreenId handleInput(input::InputEvent event) = 0;
    virtual void render(display::IDisplay& display) const = 0;
};

}  // namespace audiostreamer::ui

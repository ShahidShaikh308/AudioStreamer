#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "audiostreamer/display/IDisplay.h"
#include "audiostreamer/input/IInput.h"

namespace audiostreamer::ui {

class Menu final {
public:
    static constexpr std::size_t kMaximumItems = 16;
    static constexpr std::size_t kVisibleItems = 4;

    void setItems(const char* const* items, std::size_t count);
    void resetSelection();
    bool handleInput(input::InputEvent event, std::size_t& selectedItem);
    void render(display::IDisplay& display, const char* title) const;
    std::size_t itemCount() const;

private:
    std::array<const char*, kMaximumItems> items_{};
    std::size_t count_ = 0;
    std::size_t selected_ = 0;
    std::size_t firstVisible_ = 0;
};

}  // namespace audiostreamer::ui

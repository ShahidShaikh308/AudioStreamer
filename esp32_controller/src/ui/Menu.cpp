#include "audiostreamer/ui/Menu.h"

#include <algorithm>

#include "audiostreamer/display/DisplayFrame.h"

namespace audiostreamer::ui {

void Menu::setItems(const char* const* items, std::size_t count, bool preserveSelection) {
    const std::size_t previousSelection = selected_;
    count_ = std::min(count, kMaximumItems);
    for (std::size_t i = 0; i < count_; ++i) items_[i] = items[i];
    for (std::size_t i = count_; i < kMaximumItems; ++i) items_[i] = nullptr;
    if (!preserveSelection || count_ == 0) resetSelection();
    else {
        selected_ = std::min(previousSelection, count_ - 1);
        firstVisible_ = selected_ >= kVisibleItems ? selected_ - kVisibleItems + 1 : 0;
    }
}

void Menu::resetSelection() {
    selected_ = 0;
    firstVisible_ = 0;
}

bool Menu::handleInput(input::InputEvent event, std::size_t& selectedItem) {
    if (count_ == 0) return false;
    if (event == input::InputEvent::Up) {
        selected_ = selected_ == 0 ? count_ - 1 : selected_ - 1;
    } else if (event == input::InputEvent::Down) {
        selected_ = (selected_ + 1) % count_;
    } else if (event == input::InputEvent::Pressed) {
        selectedItem = selected_;
        return true;
    } else {
        return false;
    }

    if (selected_ < firstVisible_) firstVisible_ = selected_;
    if (selected_ >= firstVisible_ + kVisibleItems) firstVisible_ = selected_ - kVisibleItems + 1;
    return false;
}

void Menu::render(display::IDisplay& display, const char* title) const {
    display::DisplayFrame frame{};
    frame.lines[0] = title;
    frame.lineCount = 1;
    if (count_ == 0) {
        frame.lines[1] = "No items found";
        frame.lines[2] = "Press/Left: back";
        frame.lines[5] = "";
        frame.lineCount = 6;
        display.render(frame);
        return;
    }
    for (std::size_t row = 0; row < kVisibleItems && row + firstVisible_ < count_; ++row) {
        frame.lines[1 + row] = items_[row + firstVisible_];
        frame.lineCount = static_cast<std::uint8_t>(2 + row);
    }
    frame.lines[5] = count_ > kVisibleItems ? "UP/DN scroll L:back" : "UP/DN move OK L:back";
    frame.lineCount = 6;

    char* selectedLine = nullptr;
    char selectedBuffer[32]{};
    if (count_ > 0) {
        const std::size_t visibleRow = selected_ - firstVisible_;
        if (visibleRow < kVisibleItems) {
            const char* label = items_[selected_];
            std::size_t pos = 0;
            selectedBuffer[pos++] = '>';
            selectedBuffer[pos++] = ' ';
            while (label != nullptr && *label != '\0' && pos + 1 < sizeof(selectedBuffer)) {
                selectedBuffer[pos++] = *label++;
            }
            selectedBuffer[pos] = '\0';
            selectedLine = selectedBuffer;
            frame.lines[1 + visibleRow] = selectedLine;
        }
    }
    display.render(frame);
}

std::size_t Menu::itemCount() const {
    return count_;
}

}  // namespace audiostreamer::ui

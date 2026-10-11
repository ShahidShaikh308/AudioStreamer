#include "audiostreamer/display/Ssd1306Display.h"

#include "audiostreamer/config/ControllerConfig.h"

#include <Wire.h>

namespace audiostreamer::display {
namespace {

constexpr std::uint8_t kDisplayAddress = 0x3C;
constexpr std::uint8_t kTextLineHeight = 10;

}  // namespace

Ssd1306Display::Ssd1306Display() : display_(128, 64, &Wire, -1) {}

bool Ssd1306Display::begin() {
    Wire.begin(audiostreamer::config::ControllerConfig::kDisplaySdaPin,
               audiostreamer::config::ControllerConfig::kDisplaySclPin);
    if (!display_.begin(SSD1306_SWITCHCAPVCC, kDisplayAddress)) return false;
    display_.setTextSize(1);
    display_.setTextColor(SSD1306_WHITE);
    display_.setTextWrap(false);
    clear();
    return true;
}

void Ssd1306Display::clear() {
    display_.clearDisplay();
    display_.display();
}

void Ssd1306Display::render(const DisplayFrame& frame) {
    display_.clearDisplay();
    display_.setTextSize(1);
    display_.setTextColor(SSD1306_WHITE);
    display_.setTextWrap(false);

    const std::uint8_t lineCount =
        frame.lineCount < DisplayFrame::kMaximumLines ? frame.lineCount : DisplayFrame::kMaximumLines;
    for (std::uint8_t index = 0; index < lineCount; ++index) {
        if (frame.lines[index] == nullptr) continue;
        display_.setCursor(0, static_cast<std::int16_t>(index * kTextLineHeight));
        display_.print(frame.lines[index]);
    }

    display_.display();
}

}  // namespace audiostreamer::display

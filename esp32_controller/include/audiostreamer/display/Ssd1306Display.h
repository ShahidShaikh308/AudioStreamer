#pragma once

#include <Adafruit_SSD1306.h>

#include "audiostreamer/display/IDisplay.h"

namespace audiostreamer::display {

class Ssd1306Display final : public IDisplay {
public:
    Ssd1306Display();
    Ssd1306Display(const Ssd1306Display&) = delete;
    Ssd1306Display& operator=(const Ssd1306Display&) = delete;
    Ssd1306Display(Ssd1306Display&&) = delete;
    Ssd1306Display& operator=(Ssd1306Display&&) = delete;

    bool begin() override;
    void clear() override;
    void render(const DisplayFrame& frame) override;

private:
    Adafruit_SSD1306 display_;
};

}  // namespace audiostreamer::display

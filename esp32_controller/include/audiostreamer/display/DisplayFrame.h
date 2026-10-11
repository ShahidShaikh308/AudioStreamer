#pragma once

#include <cstdint>

namespace audiostreamer::display {

struct DisplayFrame final {
    static constexpr std::uint8_t kMaximumLines = 6;

    const char* lines[kMaximumLines]{};
    std::uint8_t lineCount = 0;
};

}  // namespace audiostreamer::display

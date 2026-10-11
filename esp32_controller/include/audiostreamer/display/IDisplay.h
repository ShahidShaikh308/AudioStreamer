#pragma once

#include "audiostreamer/display/DisplayFrame.h"

namespace audiostreamer::display {

class IDisplay {
public:
    virtual ~IDisplay() = default;

    virtual bool begin() = 0;
    virtual void clear() = 0;
    virtual void render(const DisplayFrame& frame) = 0;
};

}  // namespace audiostreamer::display

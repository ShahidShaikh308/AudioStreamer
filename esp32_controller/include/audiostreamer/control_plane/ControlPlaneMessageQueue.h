#pragma once

#include "audiostreamer/control_plane/IControlPlaneClient.h"

#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>

namespace audiostreamer::control_plane {

class ControlPlaneMessageQueue final {
public:
    ControlPlaneMessageQueue() = default;
    ~ControlPlaneMessageQueue();
    ControlPlaneMessageQueue(const ControlPlaneMessageQueue&) = delete;
    ControlPlaneMessageQueue& operator=(const ControlPlaneMessageQueue&) = delete;
    ControlPlaneMessageQueue(ControlPlaneMessageQueue&&) = delete;
    ControlPlaneMessageQueue& operator=(ControlPlaneMessageQueue&&) = delete;

    bool begin();
    void shutdown();
    bool publish(const ControlPlaneSnapshot& snapshot);
    bool poll(ControlPlaneSnapshot& snapshot);

private:
    QueueHandle_t queue_ = nullptr;
};

}  // namespace audiostreamer::control_plane

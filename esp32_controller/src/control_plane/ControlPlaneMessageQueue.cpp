#include "audiostreamer/control_plane/ControlPlaneMessageQueue.h"

namespace audiostreamer::control_plane {

ControlPlaneMessageQueue::~ControlPlaneMessageQueue() {
    shutdown();
}

bool ControlPlaneMessageQueue::begin() {
    if (queue_ != nullptr) return true;
    queue_ = xQueueCreate(1, sizeof(ControlPlaneSnapshot));
    return queue_ != nullptr;
}

void ControlPlaneMessageQueue::shutdown() {
    if (queue_ == nullptr) return;
    vQueueDelete(queue_);
    queue_ = nullptr;
}

bool ControlPlaneMessageQueue::publish(const ControlPlaneSnapshot& snapshot) {
    return queue_ != nullptr && xQueueOverwrite(queue_, &snapshot) == pdPASS;
}

bool ControlPlaneMessageQueue::poll(ControlPlaneSnapshot& snapshot) {
    return queue_ != nullptr && xQueueReceive(queue_, &snapshot, 0) == pdPASS;
}

}  // namespace audiostreamer::control_plane

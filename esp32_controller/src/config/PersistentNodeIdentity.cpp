#include "audiostreamer/config/PersistentNodeIdentity.h"

#include <esp_system.h>

namespace audiostreamer::config {
namespace {

constexpr char kPreferencesNamespace[] = "audiostreamer";
constexpr char kNodeIdKey[] = "node_id";

}  // namespace

PersistentNodeIdentity::~PersistentNodeIdentity() {
    if (preferencesOpen_) preferences_.end();
}

bool PersistentNodeIdentity::begin() {
    if (!preferences_.begin(kPreferencesNamespace, false)) return false;
    preferencesOpen_ = true;
    nodeId_ = preferences_.getULong64(kNodeIdKey, 0);
    if (nodeId_ != 0) return true;

    nodeId_ = (static_cast<std::uint64_t>(esp_random()) << 32U) | esp_random();
    if (nodeId_ == 0) nodeId_ = 1;
    return preferences_.putULong64(kNodeIdKey, nodeId_) == sizeof(nodeId_);
}

std::uint64_t PersistentNodeIdentity::nodeId() const noexcept {
    return nodeId_;
}

}  // namespace audiostreamer::config

#pragma once

#include <cstdint>

#include <Preferences.h>

namespace audiostreamer::config {

class PersistentNodeIdentity final {
public:
    PersistentNodeIdentity() = default;
    ~PersistentNodeIdentity();
    PersistentNodeIdentity(const PersistentNodeIdentity&) = delete;
    PersistentNodeIdentity& operator=(const PersistentNodeIdentity&) = delete;
    PersistentNodeIdentity(PersistentNodeIdentity&&) = delete;
    PersistentNodeIdentity& operator=(PersistentNodeIdentity&&) = delete;

    bool begin();
    std::uint64_t nodeId() const noexcept;

private:
    Preferences preferences_;
    std::uint64_t nodeId_ = 0;
    bool preferencesOpen_ = false;
};

}  // namespace audiostreamer::config

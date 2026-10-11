#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "audiostreamer/control_plane/PeerRecord.h"

namespace audiostreamer::control_plane {

class NodeRegistry final {
public:
    static constexpr std::size_t kCapacity = 16;

    bool upsertHello(const ControlMessage& message, const network::Ipv4Endpoint& endpoint,
                     std::uint32_t nowMs);
    bool updateHeartbeat(std::uint64_t nodeId, PeerStatus status,
                         const network::Ipv4Endpoint& endpoint, std::uint32_t nowMs);
    bool remove(std::uint64_t nodeId);
    bool expire(std::uint32_t nowMs, std::uint32_t timeoutMs);
    bool contains(std::uint64_t nodeId) const noexcept;
    bool copyPeer(std::uint64_t nodeId, PeerRecord& output) const noexcept;
    std::uint8_t size() const noexcept;
    std::size_t copyPeers(PeerRecord* output, std::size_t capacity) const noexcept;

private:
    PeerRecord* find(std::uint64_t nodeId);

    std::array<PeerRecord, kCapacity> peers_{};
};

}  // namespace audiostreamer::control_plane

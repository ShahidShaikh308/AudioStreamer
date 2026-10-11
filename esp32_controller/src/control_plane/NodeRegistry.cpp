#include "audiostreamer/control_plane/NodeRegistry.h"

#include <algorithm>
#include <cstring>

namespace audiostreamer::control_plane {

PeerRecord* NodeRegistry::find(std::uint64_t nodeId) {
    for (PeerRecord& peer : peers_) if (peer.nodeId == nodeId) return &peer;
    return nullptr;
}

bool NodeRegistry::upsertHello(const ControlMessage& message,
                               const network::Ipv4Endpoint& endpoint,
                               std::uint32_t nowMs) {
    PeerRecord* peer = find(message.nodeId);
    if (peer == nullptr) {
        for (PeerRecord& candidate : peers_) {
            if (candidate.nodeId == 0) {
                peer = &candidate;
                break;
            }
        }
    }
    if (peer == nullptr) return false;
    peer->nodeId = message.nodeId;
    const std::size_t nameSize = std::min(std::strlen(message.name), sizeof(peer->name) - 1);
    std::memcpy(peer->name, message.name, nameSize);
    peer->name[nameSize] = '\0';
    const std::size_t platformSize =
        std::min(std::strlen(message.platform), sizeof(peer->platform) - 1);
    std::memcpy(peer->platform, message.platform, platformSize);
    peer->platform[platformSize] = '\0';
    peer->capabilities = message.capabilities;
    peer->receivePort = message.receivePort;
    peer->endpoint = endpoint;
    peer->status = PeerStatus::Online;
    peer->lastSeenMs = nowMs;
    return true;
}

bool NodeRegistry::updateHeartbeat(std::uint64_t nodeId, PeerStatus status,
                                   const network::Ipv4Endpoint& endpoint, std::uint32_t nowMs) {
    PeerRecord* peer = find(nodeId);
    if (peer == nullptr) return false;
    peer->status = status;
    peer->endpoint = endpoint;
    peer->lastSeenMs = nowMs;
    return true;
}

bool NodeRegistry::remove(std::uint64_t nodeId) {
    PeerRecord* peer = find(nodeId);
    if (peer == nullptr) return false;
    *peer = {};
    return true;
}

bool NodeRegistry::expire(std::uint32_t nowMs, std::uint32_t timeoutMs) {
    bool changed = false;
    for (PeerRecord& peer : peers_) {
        if (peer.nodeId != 0 && static_cast<std::uint32_t>(nowMs - peer.lastSeenMs) >= timeoutMs) {
            peer = {};
            changed = true;
        }
    }
    return changed;
}

bool NodeRegistry::contains(std::uint64_t nodeId) const noexcept {
    for (const PeerRecord& peer : peers_) if (peer.nodeId == nodeId) return true;
    return false;
}

bool NodeRegistry::copyPeer(std::uint64_t nodeId, PeerRecord& output) const noexcept {
    for (const PeerRecord& peer : peers_) {
        if (peer.nodeId == nodeId) {
            output = peer;
            return true;
        }
    }
    return false;
}

std::uint8_t NodeRegistry::size() const noexcept {
    std::uint8_t count = 0;
    for (const PeerRecord& peer : peers_) if (peer.nodeId != 0) ++count;
    return count;
}

std::size_t NodeRegistry::copyPeers(PeerRecord* output, std::size_t capacity) const noexcept {
    if (output == nullptr) return 0;
    std::size_t copied = 0;
    for (const PeerRecord& peer : peers_) {
        if (peer.nodeId == 0 || copied >= capacity) continue;
        output[copied++] = peer;
    }
    return copied;
}

}  // namespace audiostreamer::control_plane

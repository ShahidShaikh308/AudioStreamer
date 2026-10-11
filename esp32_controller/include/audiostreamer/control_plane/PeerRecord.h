#pragma once

#include <cstdint>

#include "audiostreamer/control_plane/ControlProtocolV1.h"
#include "audiostreamer/network/IControlTransport.h"

namespace audiostreamer::control_plane {

struct PeerRecord final {
    std::uint64_t nodeId = 0;
    char name[32]{};
    char platform[16]{};
    std::uint16_t capabilities = 0;
    std::uint16_t receivePort = 0;
    PeerStatus status = PeerStatus::Online;
    network::Ipv4Endpoint endpoint{};
    std::uint32_t lastSeenMs = 0;
};

}  // namespace audiostreamer::control_plane

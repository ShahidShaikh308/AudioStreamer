#pragma once

#include <cstddef>
#include <cstdint>

#include "audiostreamer/control_plane/PeerRecord.h"

namespace audiostreamer::config {
struct ControllerConfig;
}

namespace audiostreamer::network {
class IControlTransport;
class IWifiConnection;
}

namespace audiostreamer::control_plane {

enum class ControlPlaneState : std::uint8_t {
    Stopped,
    WaitingForWifi,
    Discovering,
    Error,
};

struct ControlPlaneSnapshot final {
    static constexpr std::size_t kMaximumPeers = 16;
    static constexpr std::size_t kMaximumStreams = 8;
    struct Stream final {
        std::uint64_t senderNodeId = 0;
        std::uint64_t receiverNodeId = 0;
        std::uint16_t transactionId = 0;
        enum class State : std::uint8_t { Waiting, Streaming, Error } state = State::Waiting;
    };
    ControlPlaneState state = ControlPlaneState::Stopped;
    std::uint64_t nodeId = 0;
    std::uint8_t peerCount = 0;
    PeerRecord peers[kMaximumPeers]{};
    enum class StreamState : std::uint8_t { Idle, Waiting, Streaming, Error } streamState = StreamState::Idle;
    std::uint64_t senderNodeId = 0;
    std::uint64_t receiverNodeId = 0;
    std::uint16_t transactionId = 0;
    std::uint16_t errorCode = 0;
    char errorText[96]{};
    std::uint8_t streamCount = 0;
    Stream streams[kMaximumStreams]{};
};

class IControlPlaneClient {
public:
    virtual ~IControlPlaneClient() = default;

    virtual bool initialize(network::IControlTransport& transport,
                            network::IWifiConnection& wifiConnection,
                            const config::ControllerConfig& config) = 0;
    virtual bool pollSnapshot(ControlPlaneSnapshot& snapshot) = 0;
    virtual bool queueStartStream(std::uint64_t senderNodeId,
                                  std::uint64_t receiverNodeId) = 0;
    virtual bool queueStopStream(std::uint64_t senderNodeId,
                                 std::uint64_t receiverNodeId) = 0;
    virtual void shutdown() = 0;
};

}  // namespace audiostreamer::control_plane

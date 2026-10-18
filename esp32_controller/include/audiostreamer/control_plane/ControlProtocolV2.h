#pragma once

#include "audiostreamer/control_plane/ControlProtocolV1.h"

namespace audiostreamer::control_plane {

class ControlProtocolV2 final {
public:
    static constexpr std::size_t kNonceSize = 16;
    static constexpr std::size_t kTagSize = 32;
    static constexpr std::size_t kAuthenticatedCommandSize = ControlProtocolV1::kHeaderSize + 8 + 8 + 8 + 2 + kNonceSize + kTagSize;

    static std::size_t serializeControllerCommand(std::uint8_t* output, std::size_t capacity,
        ControlMessageType type, std::uint64_t controllerNodeId, std::uint64_t senderNodeId,
        std::uint64_t receiverNodeId, std::uint16_t transactionId,
        const std::uint8_t nonce[kNonceSize], const char* secretHex);
};

}  // namespace audiostreamer::control_plane

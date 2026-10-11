#pragma once

#include <cstddef>
#include <cstdint>

namespace audiostreamer::control_plane {

enum class ControlMessageType : std::uint8_t {
    Hello = 1, Goodbye = 2, Heartbeat = 3, StartStream = 4, StopStream = 5,
    StreamStatus = 6, Error = 7
};
enum class PeerStatus : std::uint8_t { Online = 1, Busy = 2, Streaming = 3, Error = 4 };
enum class StreamState : std::uint8_t { Running = 1, Stopped = 2, Failed = 3 };

struct ControlMessage final {
    ControlMessageType type = ControlMessageType::Hello;
    std::uint64_t nodeId = 0;
    char name[256]{};
    char platform[256]{};
    std::uint16_t capabilities = 0;
    std::uint16_t receivePort = 0;
    PeerStatus status = PeerStatus::Online;
    std::uint64_t senderNodeId = 0;
    std::uint64_t receiverNodeId = 0;
    std::uint16_t transactionId = 0;
    bool hasTransactionId = false;
    StreamState streamState = StreamState::Stopped;
    std::uint16_t errorCode = 0;
    char errorText[256]{};
};

class ControlProtocolV1 final {
public:
    static constexpr std::size_t kHeaderSize = 6;
    static constexpr std::size_t kMaximumDatagramSize = 1200;

    static std::size_t serializeHello(std::uint8_t* output, std::size_t capacity,
                                      std::uint64_t nodeId, const char* name,
                                      const char* platform, std::uint16_t capabilities,
                                      std::uint16_t receivePort);
    static std::size_t serializeHeartbeat(std::uint8_t* output, std::size_t capacity,
                                          std::uint64_t nodeId, PeerStatus status);
    static std::size_t serializeGoodbye(std::uint8_t* output, std::size_t capacity,
                                        std::uint64_t nodeId);
    static std::size_t serializeStreamCommand(std::uint8_t* output, std::size_t capacity,
                                              ControlMessageType type,
                                              std::uint64_t senderNodeId,
                                              std::uint64_t receiverNodeId,
                                              std::uint16_t transactionId);
    static bool parse(const std::uint8_t* data, std::size_t size, ControlMessage& message);
};

}  // namespace audiostreamer::control_plane

#pragma once

#include <cstddef>
#include <cstdint>

namespace audiostreamer::network {

struct Ipv4Endpoint final {
    std::uint8_t address[4]{};
    std::uint16_t port = 0;
};

class IControlTransport {
public:
    virtual ~IControlTransport() = default;

    virtual bool initialize(std::uint16_t port) = 0;
    virtual void shutdown() = 0;
    virtual bool sendBroadcast(const std::uint8_t* data, std::size_t size) = 0;
    virtual bool sendTo(const std::uint8_t* data, std::size_t size,
                        const Ipv4Endpoint& endpoint) = 0;
    virtual std::size_t receive(std::uint8_t* buffer, std::size_t capacity,
                                Ipv4Endpoint& source) = 0;
};

}  // namespace audiostreamer::network

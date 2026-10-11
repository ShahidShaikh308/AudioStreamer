#pragma once

#include <WiFiUdp.h>

#include "audiostreamer/network/IControlTransport.h"

namespace audiostreamer::network {

class UdpControlTransport final : public IControlTransport {
public:
    UdpControlTransport() = default;
    ~UdpControlTransport() override;
    UdpControlTransport(const UdpControlTransport&) = delete;
    UdpControlTransport& operator=(const UdpControlTransport&) = delete;
    UdpControlTransport(UdpControlTransport&&) = delete;
    UdpControlTransport& operator=(UdpControlTransport&&) = delete;

    bool initialize(std::uint16_t port) override;
    void shutdown() override;
    bool sendBroadcast(const std::uint8_t* data, std::size_t size) override;
    bool sendTo(const std::uint8_t* data, std::size_t size,
                const Ipv4Endpoint& endpoint) override;
    std::size_t receive(std::uint8_t* buffer, std::size_t capacity,
                        Ipv4Endpoint& source) override;

private:
    bool send(const std::uint8_t* data, std::size_t size, const IPAddress& address,
              std::uint16_t port);

    WiFiUDP socket_;
    std::uint16_t port_ = 0;
    bool initialized_ = false;
};

}  // namespace audiostreamer::network

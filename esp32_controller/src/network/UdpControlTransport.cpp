#include "audiostreamer/network/UdpControlTransport.h"

#include <array>

namespace audiostreamer::network {

UdpControlTransport::~UdpControlTransport() {
    shutdown();
}

bool UdpControlTransport::initialize(std::uint16_t port) {
    shutdown();
    if (port == 0 || socket_.begin(port) == 0) return false;
    port_ = port;
    initialized_ = true;
    return true;
}

void UdpControlTransport::shutdown() {
    if (initialized_) socket_.stop();
    initialized_ = false;
    port_ = 0;
}

bool UdpControlTransport::sendBroadcast(const std::uint8_t* data, std::size_t size) {
    return initialized_ && send(data, size, IPAddress(255, 255, 255, 255), port_);
}

bool UdpControlTransport::sendTo(const std::uint8_t* data, std::size_t size,
                                 const Ipv4Endpoint& endpoint) {
    return initialized_ && endpoint.port != 0 &&
           send(data, size, IPAddress(endpoint.address[0], endpoint.address[1],
                                      endpoint.address[2], endpoint.address[3]),
                endpoint.port);
}

std::size_t UdpControlTransport::receive(std::uint8_t* buffer, std::size_t capacity,
                                         Ipv4Endpoint& source) {
    if (!initialized_ || buffer == nullptr || capacity == 0) return 0;
    const int packetSize = socket_.parsePacket();
    if (packetSize <= 0) return 0;

    const IPAddress address = socket_.remoteIP();
    for (std::size_t index = 0; index < 4; ++index) source.address[index] = address[index];
    source.port = socket_.remotePort();

    if (static_cast<std::size_t>(packetSize) > capacity) {
        std::array<std::uint8_t, 64> discard{};
        while (socket_.available() > 0) socket_.read(discard.data(), discard.size());
        return 0;
    }
    const int received = socket_.read(buffer, static_cast<std::size_t>(packetSize));
    return received > 0 ? static_cast<std::size_t>(received) : 0;
}

bool UdpControlTransport::send(const std::uint8_t* data, std::size_t size,
                               const IPAddress& address, std::uint16_t port) {
    if (data == nullptr || size == 0 || size > 1200 ||
        socket_.beginPacket(address, port) == 0) return false;
    const std::size_t written = socket_.write(data, size);
    return socket_.endPacket() == 1 && written == size;
}

}  // namespace audiostreamer::network

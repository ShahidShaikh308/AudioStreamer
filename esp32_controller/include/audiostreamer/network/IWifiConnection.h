#pragma once

#include <cstddef>

namespace audiostreamer::network {

class IWifiConnection {
public:
    // The address pointer is valid only for the duration of the callback.
    using ConnectionCallback = void (*)(bool connected, const char* ipAddress, void* context);

    virtual ~IWifiConnection() = default;

    virtual bool begin() = 0;
    virtual void poll() = 0;
    virtual bool isConnected() const = 0;
    virtual bool currentIp(char* buffer, std::size_t capacity) const = 0;
    virtual void setConnectionCallback(ConnectionCallback callback, void* context) = 0;
};

}  // namespace audiostreamer::network

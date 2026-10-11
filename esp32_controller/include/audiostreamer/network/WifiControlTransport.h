#pragma once

#include <WiFi.h>

#include <freertos/FreeRTOS.h>

#include "audiostreamer/config/ControllerConfig.h"
#include "audiostreamer/network/IWifiConnection.h"

namespace audiostreamer::network {

class WifiControlTransport final : public IWifiConnection {
public:
    explicit WifiControlTransport(const config::ControllerConfig& config);
    ~WifiControlTransport() override;
    WifiControlTransport(const WifiControlTransport&) = delete;
    WifiControlTransport& operator=(const WifiControlTransport&) = delete;
    WifiControlTransport(WifiControlTransport&&) = delete;
    WifiControlTransport& operator=(WifiControlTransport&&) = delete;

    bool begin() override;
    void poll() override;
    bool isConnected() const override;
    bool currentIp(char* buffer, std::size_t capacity) const override;
    void setConnectionCallback(ConnectionCallback callback, void* context) override;

private:
    void handleWifiEvent(arduino_event_id_t event);
    void notifyConnectionState(bool connected);

    const config::ControllerConfig& config_;
    portMUX_TYPE callbackMux_ = portMUX_INITIALIZER_UNLOCKED;
    ConnectionCallback callback_ = nullptr;
    void* callbackContext_ = nullptr;
    wifi_event_id_t eventHandlerId_ = 0;
    std::uint32_t lastReconnectAttemptMs_ = 0;
    bool eventHandlerRegistered_ = false;
};

}  // namespace audiostreamer::network

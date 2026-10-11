#include "audiostreamer/network/WifiControlTransport.h"

#include <Arduino.h>

#include <cstdio>

#if __has_include("audiostreamer/config/WifiSettings.h")
#include "audiostreamer/config/WifiSettings.h"
#else
#define AUDIOSTREAMER_WIFI_SSID ""
#define AUDIOSTREAMER_WIFI_PASSWORD ""
#endif

#ifndef AUDIOSTREAMER_WIFI_SSID
#define AUDIOSTREAMER_WIFI_SSID audiostreamer::config::kWifiSsid
#endif
#ifndef AUDIOSTREAMER_WIFI_PASSWORD
#define AUDIOSTREAMER_WIFI_PASSWORD audiostreamer::config::kWifiPassword
#endif

namespace audiostreamer::network {
namespace {

constexpr std::size_t kIpAddressCapacity = 16;

void formatIpAddress(const IPAddress& address, char* buffer, std::size_t capacity) {
    if (buffer == nullptr || capacity == 0) return;
    std::snprintf(buffer, capacity, "%u.%u.%u.%u", address[0], address[1], address[2], address[3]);
}

}  // namespace

WifiControlTransport::WifiControlTransport(const config::ControllerConfig& config)
    : config_(config) {}

WifiControlTransport::~WifiControlTransport() {
    if (eventHandlerRegistered_) WiFi.removeEvent(eventHandlerId_);
}

bool WifiControlTransport::begin() {
    if (AUDIOSTREAMER_WIFI_SSID[0] == '\0') return false;

    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    eventHandlerId_ = WiFi.onEvent([this](arduino_event_id_t event, arduino_event_info_t) {
        handleWifiEvent(event);
    });
    eventHandlerRegistered_ = true;
    lastReconnectAttemptMs_ = millis();
    return WiFi.begin(AUDIOSTREAMER_WIFI_SSID, AUDIOSTREAMER_WIFI_PASSWORD) != WL_CONNECT_FAILED;
}

void WifiControlTransport::poll() {
    if (WiFi.isConnected()) return;

    const std::uint32_t now = millis();
    if (static_cast<std::uint32_t>(now - lastReconnectAttemptMs_) <
        config_.wifiReconnectIntervalMs) return;

    lastReconnectAttemptMs_ = now;
    WiFi.reconnect();
}

bool WifiControlTransport::isConnected() const {
    return WiFi.isConnected();
}

bool WifiControlTransport::currentIp(char* buffer, std::size_t capacity) const {
    if (buffer == nullptr || capacity < kIpAddressCapacity || !WiFi.isConnected()) return false;
    formatIpAddress(WiFi.localIP(), buffer, capacity);
    return true;
}

void WifiControlTransport::setConnectionCallback(ConnectionCallback callback, void* context) {
    portENTER_CRITICAL(&callbackMux_);
    callback_ = callback;
    callbackContext_ = context;
    portEXIT_CRITICAL(&callbackMux_);
}

void WifiControlTransport::handleWifiEvent(arduino_event_id_t event) {
    if (event == ARDUINO_EVENT_WIFI_STA_GOT_IP) {
        notifyConnectionState(true);
    } else if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED ||
               event == ARDUINO_EVENT_WIFI_STA_LOST_IP) {
        notifyConnectionState(false);
    }
}

void WifiControlTransport::notifyConnectionState(bool connected) {
    ConnectionCallback callback = nullptr;
    void* context = nullptr;
    portENTER_CRITICAL(&callbackMux_);
    callback = callback_;
    context = callbackContext_;
    portEXIT_CRITICAL(&callbackMux_);
    if (callback == nullptr) return;

    char ipAddress[kIpAddressCapacity]{};
    if (connected) formatIpAddress(WiFi.localIP(), ipAddress, sizeof(ipAddress));
    callback(connected, ipAddress, context);
}

}  // namespace audiostreamer::network

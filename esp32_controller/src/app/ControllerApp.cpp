#include "audiostreamer/app/ControllerApp.h"

#include <Arduino.h>

#include <cstdio>

namespace audiostreamer::app {

ControllerApp::ControllerApp(const config::ControllerConfig& controllerConfig)
    : controllerConfig_(controllerConfig),
      input_(controllerConfig),
      wifiConnection_(controllerConfig) {}

ControllerApp::~ControllerApp() {
    shutdown();
}

bool ControllerApp::initialize() {
    Serial.println("AudioStreamer: initializing OLED...");
    if (!display_.begin()) {
        Serial.println("AudioStreamer: OLED init failed (check power, SDA/SCL, and address 0x3C).");
        return false;
    }
    Serial.println("AudioStreamer: OLED ready.");
    if (!input_.begin()) {
        Serial.println("AudioStreamer: joystick init failed.");
        return false;
    }
    if (!wifiConnection_.begin()) {
        Serial.println("AudioStreamer: Wi-Fi did not start; check WifiSettings.h. Retrying in background.");
    }
    if (!controlPlane_.initialize(controlTransport_, wifiConnection_, controllerConfig_)) {
        Serial.println("AudioStreamer: control plane init failed.");
        return false;
    }
    ui_.setNetworkInfo(nullptr, controllerConfig_.controlPort, controllerConfig_.receivePort);
    if (!ui_.initialize()) {
        Serial.println("AudioStreamer: UI init failed.");
        return false;
    }

    initialized_ = true;
    lastDisplayUpdateMs_ = millis() - 250;
    ui_.render(display_);
    return true;
}

input::InputState ControllerApp::poll() {
    if (!initialized_) return {};
    wifiConnection_.poll();
    const input::InputState inputState = input_.poll();
    bool receivedSnapshot = false;
    while (controlPlane_.pollSnapshot(controlSnapshot_)) receivedSnapshot = true;
    char localIp[16]{};
    wifiConnection_.currentIp(localIp, sizeof(localIp));
    ui_.setNetworkInfo(localIp, controllerConfig_.controlPort, controllerConfig_.receivePort);
    ui_.updateStatus(wifiConnection_.isConnected(),
                     controlSnapshot_.state == control_plane::ControlPlaneState::Discovering,
                     controlSnapshot_.peerCount);
    if (receivedSnapshot) {
        ui::UiPeer uiPeers[control_plane::ControlPlaneSnapshot::kMaximumPeers]{};
        for (std::size_t i = 0; i < controlSnapshot_.peerCount; ++i) {
            uiPeers[i].nodeId = controlSnapshot_.peers[i].nodeId;
            uiPeers[i].capabilities = controlSnapshot_.peers[i].capabilities;
            std::snprintf(uiPeers[i].name, sizeof(uiPeers[i].name), "%s",
                          controlSnapshot_.peers[i].name);
            std::snprintf(uiPeers[i].platform, sizeof(uiPeers[i].platform), "%s",
                          controlSnapshot_.peers[i].platform);
            const char* status = "ONLINE";
            switch (controlSnapshot_.peers[i].status) {
                case control_plane::PeerStatus::Online: status = "ONLINE"; break;
                case control_plane::PeerStatus::Busy: status = "BUSY"; break;
                case control_plane::PeerStatus::Streaming: status = "STREAMING"; break;
                case control_plane::PeerStatus::Error: status = "ERROR"; break;
            }
            std::snprintf(uiPeers[i].status, sizeof(uiPeers[i].status), "%s", status);
            std::snprintf(uiPeers[i].address, sizeof(uiPeers[i].address), "%u.%u.%u.%u",
                          controlSnapshot_.peers[i].endpoint.address[0],
                          controlSnapshot_.peers[i].endpoint.address[1],
                          controlSnapshot_.peers[i].endpoint.address[2],
                          controlSnapshot_.peers[i].endpoint.address[3]);
            uiPeers[i].receivePort = controlSnapshot_.peers[i].receivePort;
        }
        ui_.setPeers(uiPeers, controlSnapshot_.peerCount);
        ui::StreamUiState streamState = ui::StreamUiState::Idle;
        switch (controlSnapshot_.streamState) {
            case control_plane::ControlPlaneSnapshot::StreamState::Idle:
                streamState = ui::StreamUiState::Idle;
                break;
            case control_plane::ControlPlaneSnapshot::StreamState::Waiting:
                streamState = ui::StreamUiState::Waiting;
                break;
            case control_plane::ControlPlaneSnapshot::StreamState::Streaming:
                streamState = ui::StreamUiState::Streaming;
                break;
            case control_plane::ControlPlaneSnapshot::StreamState::Error:
                streamState = ui::StreamUiState::Error;
                break;
        }
        ui_.setStreamState(streamState, controlSnapshot_.errorText[0] == '\0'
                                           ? nullptr : controlSnapshot_.errorText);
        ui::UiRoute uiRoutes[control_plane::ControlPlaneSnapshot::kMaximumStreams]{};
        for (std::size_t i = 0; i < controlSnapshot_.streamCount; ++i) {
            uiRoutes[i].senderNodeId = controlSnapshot_.streams[i].senderNodeId;
            uiRoutes[i].receiverNodeId = controlSnapshot_.streams[i].receiverNodeId;
            switch (controlSnapshot_.streams[i].state) {
                case control_plane::ControlPlaneSnapshot::Stream::State::Waiting:
                    uiRoutes[i].state = ui::StreamUiState::Waiting; break;
                case control_plane::ControlPlaneSnapshot::Stream::State::Streaming:
                    uiRoutes[i].state = ui::StreamUiState::Streaming; break;
                case control_plane::ControlPlaneSnapshot::Stream::State::Error:
                    uiRoutes[i].state = ui::StreamUiState::Error; break;
            }
        }
        ui_.setRoutes(uiRoutes, controlSnapshot_.streamCount);
    }
    if (inputState.up) ui_.handleInput(input::InputEvent::Up);
    if (inputState.down) ui_.handleInput(input::InputEvent::Down);
    if (inputState.left) ui_.handleInput(input::InputEvent::Left);
    if (inputState.right) ui_.handleInput(input::InputEvent::Right);
    if (inputState.pressed) ui_.handleInput(input::InputEvent::Pressed);
    if (inputState.released) ui_.handleInput(input::InputEvent::Released);

    const ui::UiAction action = ui_.takeAction();
    if (action.kind == ui::UiActionKind::StartStream) {
        if (controlPlane_.queueStartStream(action.senderNodeId, action.receiverNodeId)) {
            ui_.setStreamState(ui::StreamUiState::Waiting);
        } else {
            ui_.setStreamState(ui::StreamUiState::Error, "Start request could not queue");
        }
    } else if (action.kind == ui::UiActionKind::StopStream) {
        if (controlPlane_.queueStopStream(action.senderNodeId, action.receiverNodeId)) {
            ui_.setStreamState(ui::StreamUiState::Waiting);
        } else {
            ui_.setStreamState(ui::StreamUiState::Error, "Stop request could not queue");
        }
    }

    const std::uint32_t now = millis();
    if (static_cast<std::uint32_t>(now - lastDisplayUpdateMs_) >= 250) {
        ui_.render(display_);
        lastDisplayUpdateMs_ = now;
    }
    return inputState;
}

bool ControllerApp::isInitialized() const {
    return initialized_;
}

void ControllerApp::shutdown() {
    if (!initialized_) return;
    controlPlane_.shutdown();
    display_.clear();
    initialized_ = false;
}

}  // namespace audiostreamer::app

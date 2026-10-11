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
    if (!display_.begin()) return false;
    if (!input_.begin()) return false;
    wifiConnection_.begin();
    if (!controlPlane_.initialize(controlTransport_, wifiConnection_, controllerConfig_)) return false;
    if (!ui_.initialize()) return false;

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
    ui_.updateStatus(wifiConnection_.isConnected(), controlSnapshot_.peerCount);
    if (receivedSnapshot) {
        ui::UiPeer uiPeers[control_plane::ControlPlaneSnapshot::kMaximumPeers]{};
        for (std::size_t i = 0; i < controlSnapshot_.peerCount; ++i) {
            uiPeers[i].nodeId = controlSnapshot_.peers[i].nodeId;
            uiPeers[i].capabilities = controlSnapshot_.peers[i].capabilities;
            std::snprintf(uiPeers[i].name, sizeof(uiPeers[i].name), "%s",
                          controlSnapshot_.peers[i].name);
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

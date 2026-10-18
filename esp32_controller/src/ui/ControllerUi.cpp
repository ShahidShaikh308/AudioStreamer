#include "audiostreamer/ui/ControllerUi.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace audiostreamer::ui {

ControllerUi::ControllerUi()
    : connectingScreen_(model_),
      discoveringScreen_(model_),
      mainMenuScreen_(model_),
      senderSelectionScreen_(model_),
      receiverSelectionScreen_(model_),
      confirmScreen_(model_),
      streamingScreen_(model_),
      activeRoutesScreen_(model_),
      settingsScreen_(model_),
      deviceListScreen_(model_),
      deviceDetailsScreen_(model_) {}

bool ControllerUi::initialize() {
    transition(ScreenId::Boot);
    return currentScreen_ != nullptr;
}

void ControllerUi::handleInput(input::InputEvent event) {
    if (currentScreen_ == nullptr) return;
    transition(currentScreen_->handleInput(event));
}

void ControllerUi::render(display::IDisplay& display) {
    if (currentScreen_ != nullptr) currentScreen_->render(display);
}

void ControllerUi::updateStatus(bool wifiConnected, bool controlReady, std::uint8_t peerCount) {
    model_.wifiConnected = wifiConnected;
    model_.controlReady = controlReady;
    model_.peerCount = std::min<std::size_t>(peerCount, model_.peers.size());
    if (!wifiConnected) {
        transition(ScreenId::Connecting);
    } else if (currentId_ == ScreenId::Boot || currentId_ == ScreenId::Connecting ||
               currentId_ == ScreenId::Discovering) {
        transition(ScreenId::MainMenu);
    }
}

void ControllerUi::setNetworkInfo(const char* localIp, std::uint16_t controlPort,
                                  std::uint16_t audioPort) {
    std::snprintf(model_.localIp, sizeof(model_.localIp), "%s",
                  localIp == nullptr || localIp[0] == '\0' ? "Unavailable" : localIp);
    model_.controlPort = controlPort;
    model_.audioPort = audioPort;
}

void ControllerUi::setPeers(const UiPeer* peers, std::size_t count) {
    const std::size_t boundedCount = std::min(count, model_.peers.size());
    model_.peerCount = peers == nullptr ? 0 : boundedCount;
    for (std::size_t i = 0; i < model_.peerCount; ++i) {
        model_.peers[i].nodeId = peers[i].nodeId;
        model_.peers[i].capabilities = peers[i].capabilities;
        std::memcpy(model_.peers[i].name, peers[i].name, sizeof(model_.peers[i].name));
        model_.peers[i].name[sizeof(model_.peers[i].name) - 1] = '\0';
        std::memcpy(model_.peers[i].platform, peers[i].platform, sizeof(model_.peers[i].platform));
        model_.peers[i].platform[sizeof(model_.peers[i].platform) - 1] = '\0';
        std::memcpy(model_.peers[i].status, peers[i].status, sizeof(model_.peers[i].status));
        model_.peers[i].status[sizeof(model_.peers[i].status) - 1] = '\0';
        std::memcpy(model_.peers[i].address, peers[i].address, sizeof(model_.peers[i].address));
        model_.peers[i].address[sizeof(model_.peers[i].address) - 1] = '\0';
        model_.peers[i].receivePort = peers[i].receivePort;
    }
    for (std::size_t i = model_.peerCount; i < model_.peers.size(); ++i) model_.peers[i] = {};
}

UiAction ControllerUi::takeAction() {
    const UiAction action = model_.pendingAction;
    model_.pendingAction = {};
    return action;
}

void ControllerUi::setStreamState(StreamUiState state, const char* errorText) {
    model_.streamState = state;
    if (errorText == nullptr) {
        model_.errorText[0] = '\0';
    } else {
        std::snprintf(model_.errorText, sizeof(model_.errorText), "%s", errorText);
    }
}

void ControllerUi::setRoutes(const UiRoute* routes, std::size_t count) {
    model_.routeCount = routes == nullptr ? 0 : std::min(count, model_.routes.size());
    for (std::size_t i = 0; i < model_.routeCount; ++i) model_.routes[i] = routes[i];
    for (std::size_t i = model_.routeCount; i < model_.routes.size(); ++i) model_.routes[i] = {};
    if (currentId_ == ScreenId::ActiveRoutes) activeRoutesScreen_.enter();
    bool selectedRouteFound = false;
    for (std::size_t i = 0; i < model_.routeCount; ++i) {
        if (model_.routes[i].senderNodeId == model_.selectedSenderId &&
            model_.routes[i].receiverNodeId == model_.selectedReceiverId) {
            model_.streamState = model_.routes[i].state;
            selectedRouteFound = true;
            break;
        }
    }
    if (!selectedRouteFound && model_.selectedSenderId != 0) model_.streamState = StreamUiState::Idle;
}

void ControllerUi::transition(ScreenId next) {
    if (next == currentId_ && currentScreen_ != nullptr) return;
    Screen* nextScreen = screenFor(next);
    if (nextScreen == nullptr) return;
    if (currentScreen_ != nullptr) currentScreen_->leave();
    currentId_ = next;
    currentScreen_ = nextScreen;
    currentScreen_->enter();
}

Screen* ControllerUi::screenFor(ScreenId id) {
    switch (id) {
        case ScreenId::Boot: return &bootScreen_;
        case ScreenId::Connecting: return &connectingScreen_;
        case ScreenId::Discovering: return &discoveringScreen_;
        case ScreenId::MainMenu: return &mainMenuScreen_;
        case ScreenId::SenderSelection: return &senderSelectionScreen_;
        case ScreenId::ReceiverSelection: return &receiverSelectionScreen_;
        case ScreenId::Confirm: return &confirmScreen_;
        case ScreenId::Streaming: return &streamingScreen_;
        case ScreenId::ActiveRoutes: return &activeRoutesScreen_;
        case ScreenId::Settings: return &settingsScreen_;
        case ScreenId::Devices: return &deviceListScreen_;
        case ScreenId::DeviceDetails: return &deviceDetailsScreen_;
    }
    return nullptr;
}

}  // namespace audiostreamer::ui

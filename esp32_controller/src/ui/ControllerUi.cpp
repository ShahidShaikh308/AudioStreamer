#include "audiostreamer/ui/ControllerUi.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace audiostreamer::ui {

ControllerUi::ControllerUi()
    : connectingScreen_(model_),
      discoveringScreen_(model_),
      senderSelectionScreen_(model_),
      receiverSelectionScreen_(model_),
      confirmScreen_(model_),
      streamingScreen_(model_) {}

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

void ControllerUi::updateStatus(bool wifiConnected, std::uint8_t peerCount) {
    model_.wifiConnected = wifiConnected;
    model_.peerCount = std::min<std::size_t>(peerCount, model_.peers.size());
    if (!wifiConnected) {
        transition(ScreenId::Connecting);
    } else if (currentId_ == ScreenId::Boot || currentId_ == ScreenId::Connecting) {
        transition(ScreenId::Discovering);
    }
}

void ControllerUi::setPeers(const UiPeer* peers, std::size_t count) {
    const std::size_t boundedCount = std::min(count, model_.peers.size());
    model_.peerCount = peers == nullptr ? 0 : boundedCount;
    for (std::size_t i = 0; i < model_.peerCount; ++i) {
        model_.peers[i].nodeId = peers[i].nodeId;
        model_.peers[i].capabilities = peers[i].capabilities;
        std::memcpy(model_.peers[i].name, peers[i].name, sizeof(model_.peers[i].name));
        model_.peers[i].name[sizeof(model_.peers[i].name) - 1] = '\0';
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
        case ScreenId::Settings: return &settingsScreen_;
    }
    return nullptr;
}

}  // namespace audiostreamer::ui

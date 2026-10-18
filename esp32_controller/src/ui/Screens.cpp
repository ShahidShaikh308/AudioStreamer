#include "audiostreamer/ui/Screens.h"

#include <cstdio>
#include <algorithm>

#include "audiostreamer/display/DisplayFrame.h"

namespace audiostreamer::ui {
namespace {

constexpr std::uint16_t kSenderCapability = 0x0004U;
constexpr std::uint16_t kReceiverCapability = 0x0008U;
constexpr const char* kMainMenuItems[] = {
    "New audio route", "Nearby devices", "Active route", "Settings"};

void renderLines(display::IDisplay& display, const char* const* lines, std::uint8_t count) {
    display::DisplayFrame frame{};
    frame.lineCount = count;
    for (std::uint8_t i = 0; i < count && i < display::DisplayFrame::kMaximumLines; ++i) {
        frame.lines[i] = lines[i];
    }
    display.render(frame);
}

const char* shortStatus(const char* status) {
    if (status[0] == 'B') return "BUSY";
    if (status[0] == 'S') return "STR";
    if (status[0] == 'E') return "ERR";
    return "ON";
}

const char* streamStateName(StreamUiState state) {
    switch (state) {
        case StreamUiState::Idle: return "Idle";
        case StreamUiState::Waiting: return "Starting...";
        case StreamUiState::Streaming: return "Streaming";
        case StreamUiState::Error: return "Failed";
    }
    return "Unknown";
}

}  // namespace

void BootScreen::enter() {}
void BootScreen::leave() {}
ScreenId BootScreen::handleInput(input::InputEvent) { return ScreenId::Boot; }
void BootScreen::render(display::IDisplay& display) const {
    const char* lines[] = {"AudioStreamer", "Starting controller..."};
    renderLines(display, lines, 2);
}

ConnectingScreen::ConnectingScreen(UiModel& model) : model_(model) {}
void ConnectingScreen::enter() {}
void ConnectingScreen::leave() {}
ScreenId ConnectingScreen::handleInput(input::InputEvent) { return ScreenId::Connecting; }
void ConnectingScreen::render(display::IDisplay& display) const {
    const char* lines[] = {"AudioStreamer", "WiFi disconnected", "Reconnecting...", "Check hotspot"};
    renderLines(display, lines, 4);
}

DiscoveringScreen::DiscoveringScreen(UiModel& model) : model_(model) {}
void DiscoveringScreen::enter() {}
void DiscoveringScreen::leave() {}
ScreenId DiscoveringScreen::handleInput(input::InputEvent event) {
    return event == input::InputEvent::Pressed ? ScreenId::MainMenu : ScreenId::Discovering;
}
void DiscoveringScreen::render(display::IDisplay& display) const {
    char peersLine[24]{};
    std::snprintf(peersLine, sizeof(peersLine), "Devices found: %u",
                  static_cast<unsigned>(model_.peerCount));
    const char* lines[] = {"Network connected", "Finding devices...", peersLine, "OK: continue"};
    renderLines(display, lines, 4);
}

MainMenuScreen::MainMenuScreen(UiModel& model) : model_(model) {}
void MainMenuScreen::enter() { menu_.setItems(kMainMenuItems, 4); }
void MainMenuScreen::leave() {}
ScreenId MainMenuScreen::handleInput(input::InputEvent event) {
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) return ScreenId::MainMenu;
    switch (selected) {
        case 0: return ScreenId::SenderSelection;
        case 1: return ScreenId::Devices;
        case 2: return ScreenId::ActiveRoutes;
        case 3: return ScreenId::Settings;
        default: return ScreenId::MainMenu;
    }
}
void MainMenuScreen::render(display::IDisplay& display) const {
    char title[24]{};
    std::snprintf(title, sizeof(title), "W:%s C:%s N:%02u",
                  model_.wifiConnected ? "ON" : "OFF",
                  model_.controlReady ? "OK" : "WAIT",
                  static_cast<unsigned>(model_.peerCount));
    menu_.render(display, title);
}

PeerSelectionScreen::PeerSelectionScreen(UiModel& model, bool selectSender)
    : model_(model), selectSender_(selectSender) {}
void PeerSelectionScreen::enter() {
    std::size_t count = 0;
    const std::uint16_t requiredCapability = selectSender_ ? kSenderCapability : kReceiverCapability;
    for (std::size_t i = 0; i < model_.peerCount && count < labels_.size(); ++i) {
        const UiPeer& peer = model_.peers[i];
        if ((peer.capabilities & requiredCapability) == 0) continue;
        if (!selectSender_ && peer.nodeId == model_.selectedSenderId) continue;
        std::snprintf(labels_[count].data(), labels_[count].size(), "%.25s %s",
                      peer.name, shortStatus(peer.status));
        labelPointers_[count] = labels_[count].data();
        peerIndices_[count] = i;
        ++count;
    }
    menu_.setItems(labelPointers_.data(), count);
}
void PeerSelectionScreen::leave() {}
ScreenId PeerSelectionScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) {
        if (selectSender_) return ScreenId::MainMenu;
        return ScreenId::SenderSelection;
    }
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) {
        if (event == input::InputEvent::Pressed && menu_.itemCount() == 0) {
            return selectSender_ ? ScreenId::MainMenu : ScreenId::SenderSelection;
        }
        return selectSender_ ? ScreenId::SenderSelection : ScreenId::ReceiverSelection;
    }
    const UiPeer& peer = model_.peers[peerIndices_[selected]];
    if (selectSender_) {
        std::snprintf(model_.selectedSender, sizeof(model_.selectedSender), "%s", peer.name);
        model_.selectedSenderId = peer.nodeId;
        model_.selectedReceiverId = 0;
        std::snprintf(model_.selectedReceiver, sizeof(model_.selectedReceiver), "None");
        return ScreenId::ReceiverSelection;
    }
    std::snprintf(model_.selectedReceiver, sizeof(model_.selectedReceiver), "%s", peer.name);
    model_.selectedReceiverId = peer.nodeId;
    model_.errorText[0] = '\0';
    return ScreenId::Confirm;
}
void PeerSelectionScreen::render(display::IDisplay& display) const {
    char title[24]{};
    if (selectSender_) std::snprintf(title, sizeof(title), "Choose audio source");
    else std::snprintf(title, sizeof(title), "Choose destination");
    menu_.render(display, title);
}
SenderSelectionScreen::SenderSelectionScreen(UiModel& model) : PeerSelectionScreen(model, true) {}
ReceiverSelectionScreen::ReceiverSelectionScreen(UiModel& model) : PeerSelectionScreen(model, false) {}

ConfirmScreen::ConfirmScreen(UiModel& model) : model_(model) {}
void ConfirmScreen::enter() {}
void ConfirmScreen::leave() {}
ScreenId ConfirmScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) return ScreenId::ReceiverSelection;
    if (event != input::InputEvent::Pressed) return ScreenId::Confirm;
    if (model_.selectedSenderId == 0 || model_.selectedReceiverId == 0 ||
        model_.selectedSenderId == model_.selectedReceiverId) {
        std::snprintf(model_.errorText, sizeof(model_.errorText), "Choose two devices");
        model_.streamState = StreamUiState::Error;
        return ScreenId::Confirm;
    }
    model_.pendingAction = {UiActionKind::StartStream,
                            model_.selectedSenderId, model_.selectedReceiverId};
    model_.streamState = StreamUiState::Waiting;
    model_.errorText[0] = '\0';
    return ScreenId::Streaming;
}
void ConfirmScreen::render(display::IDisplay& display) const {
    const char* startPrompt = model_.errorText[0] == '\0' ? "OK: start  L: edit" : model_.errorText;
    const char* lines[] = {"Start this route?", "From:", model_.selectedSender,
                           "To:", model_.selectedReceiver, startPrompt};
    renderLines(display, lines, 6);
}

StreamingScreen::StreamingScreen(UiModel& model) : model_(model) {}
void StreamingScreen::enter() {}
void StreamingScreen::leave() {}
ScreenId StreamingScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left)
        return model_.selectedSenderId != 0 ? ScreenId::ActiveRoutes : ScreenId::MainMenu;
    if (event != input::InputEvent::Pressed) return ScreenId::Streaming;
    if (model_.streamState == StreamUiState::Idle && model_.selectedSenderId != 0)
        return ScreenId::ActiveRoutes;
    if (model_.selectedSenderId != 0 && model_.selectedReceiverId != 0) {
        model_.pendingAction = {UiActionKind::StopStream,
                                model_.selectedSenderId, model_.selectedReceiverId};
        model_.streamState = StreamUiState::Waiting;
        model_.errorText[0] = '\0';
        return ScreenId::Streaming;
    }
    return ScreenId::ActiveRoutes;
}

ActiveRoutesScreen::ActiveRoutesScreen(UiModel& model) : model_(model) {}
void ActiveRoutesScreen::enter() {
    const std::size_t count = std::min(model_.routeCount, labels_.size());
    for (std::size_t i = 0; i < count; ++i) {
        const UiRoute& route = model_.routes[i];
        const char* sender = "?";
        const char* receiver = "?";
        for (std::size_t peer = 0; peer < model_.peerCount; ++peer) {
            if (model_.peers[peer].nodeId == route.senderNodeId) sender = model_.peers[peer].name;
            if (model_.peers[peer].nodeId == route.receiverNodeId) receiver = model_.peers[peer].name;
        }
        std::snprintf(labels_[i].data(), labels_[i].size(), "%.12s>%.12s %s",
                      sender, receiver, route.state == StreamUiState::Streaming ? "ON" :
                      route.state == StreamUiState::Waiting ? "..." : "ERR");
        labelPointers_[i] = labels_[i].data();
    }
    menu_.setItems(labelPointers_.data(), count, true);
}
void ActiveRoutesScreen::leave() {}
ScreenId ActiveRoutesScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) return ScreenId::MainMenu;
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) return ScreenId::ActiveRoutes;
    model_.selectedRouteIndex = selected;
    const UiRoute& route = model_.routes[selected];
    model_.selectedSenderId = route.senderNodeId;
    model_.selectedReceiverId = route.receiverNodeId;
    for (std::size_t i = 0; i < model_.peerCount; ++i) {
        if (model_.peers[i].nodeId == route.senderNodeId)
            std::snprintf(model_.selectedSender, sizeof(model_.selectedSender), "%s", model_.peers[i].name);
        if (model_.peers[i].nodeId == route.receiverNodeId)
            std::snprintf(model_.selectedReceiver, sizeof(model_.selectedReceiver), "%s", model_.peers[i].name);
    }
    model_.streamState = route.state;
    model_.errorText[0] = '\0';
    return ScreenId::Streaming;
}
void ActiveRoutesScreen::render(display::IDisplay& display) const {
    menu_.render(display, "Active routes");
}
void StreamingScreen::render(display::IDisplay& display) const {
    const char* state = streamStateName(model_.streamState);
    const bool routeSelected = model_.selectedSenderId != 0 && model_.selectedReceiverId != 0;
    const char* sender = routeSelected ? model_.selectedSender : "Not selected";
    const char* receiver = routeSelected ? model_.selectedReceiver : "Not selected";
    const char* action = model_.streamState == StreamUiState::Idle && routeSelected ? "Route stopped · OK: list" :
                         model_.streamState == StreamUiState::Waiting ? "Stopping or starting..." :
                         routeSelected ? "OK: stop route" : "OK: create route";
    const char* details = model_.errorText[0] == '\0' ? action : model_.errorText;
    const char* lines[] = {"Active route", state, "From:", sender, "To:", details};
    renderLines(display, lines, 6);
}

SettingsScreen::SettingsScreen(UiModel& model) : model_(model) {}
void SettingsScreen::enter() {}
void SettingsScreen::leave() {}
ScreenId SettingsScreen::handleInput(input::InputEvent event) {
    return event == input::InputEvent::Left || event == input::InputEvent::Pressed
               ? ScreenId::MainMenu : ScreenId::Settings;
}
void SettingsScreen::render(display::IDisplay& display) const {
    char status[24]{};
    char peers[24]{};
    char ip[24]{};
    char ports[24]{};
    std::snprintf(status, sizeof(status), "WiFi %s  Control %s",
                  model_.wifiConnected ? "ON" : "OFF",
                  model_.controlReady ? "READY" : "WAIT");
    std::snprintf(peers, sizeof(peers), "Nearby devices: %u",
                  static_cast<unsigned>(model_.peerCount));
    std::snprintf(ip, sizeof(ip), "IP: %.15s", model_.localIp);
    std::snprintf(ports, sizeof(ports), "Cmd %u  Audio %u",
                  static_cast<unsigned>(model_.controlPort),
                  static_cast<unsigned>(model_.audioPort));
    const char* lines[] = {"Controller status", status, peers, ip, ports, "Left/OK: home"};
    renderLines(display, lines, 6);
}

DeviceListScreen::DeviceListScreen(UiModel& model) : model_(model) {}
void DeviceListScreen::enter() {
    for (std::size_t i = 0; i < model_.peerCount; ++i) {
        std::snprintf(labels_[i].data(), labels_[i].size(), "%.25s %s",
                      model_.peers[i].name, shortStatus(model_.peers[i].status));
        labelPointers_[i] = labels_[i].data();
    }
    menu_.setItems(labelPointers_.data(), model_.peerCount);
}
void DeviceListScreen::leave() {}
ScreenId DeviceListScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) return ScreenId::MainMenu;
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) {
        if (event == input::InputEvent::Pressed && menu_.itemCount() == 0) return ScreenId::MainMenu;
        return ScreenId::Devices;
    }
    model_.selectedPeerIndex = selected;
    return ScreenId::DeviceDetails;
}
void DeviceListScreen::render(display::IDisplay& display) const {
    if (model_.peerCount == 0) {
        const char* lines[] = {"Nearby devices", "No devices found", "Check same WiFi", "Left: home"};
        renderLines(display, lines, 4);
        return;
    }
    menu_.render(display, "Nearby devices");
}

DeviceDetailsScreen::DeviceDetailsScreen(UiModel& model) : model_(model) {}
void DeviceDetailsScreen::enter() {}
void DeviceDetailsScreen::leave() {}
ScreenId DeviceDetailsScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left || event == input::InputEvent::Pressed) return ScreenId::Devices;
    return ScreenId::DeviceDetails;
}
void DeviceDetailsScreen::render(display::IDisplay& display) const {
    if (model_.selectedPeerIndex >= model_.peerCount) {
        const char* lines[] = {"Device details", "Device no longer online", "Left: devices"};
        renderLines(display, lines, 3);
        return;
    }
    const UiPeer& peer = model_.peers[model_.selectedPeerIndex];
    char platform[24]{};
    char endpoint[24]{};
    char capabilities[24]{};
    const bool canSend = (peer.capabilities & kSenderCapability) != 0;
    const bool canReceive = (peer.capabilities & kReceiverCapability) != 0;
    std::snprintf(platform, sizeof(platform), "%.13s  %.9s", peer.platform, peer.status);
    std::snprintf(endpoint, sizeof(endpoint), "%.15s:%u", peer.address,
                  static_cast<unsigned>(peer.receivePort));
    std::snprintf(capabilities, sizeof(capabilities), "%s%s%s",
                  canSend ? "Send" : "", canSend && canReceive ? " / " : "",
                  canReceive ? "Receive" : "No audio");
    const char* lines[] = {"Device details", peer.name, platform, endpoint,
                           capabilities, "Left/OK: devices"};
    renderLines(display, lines, 6);
}

}  // namespace audiostreamer::ui

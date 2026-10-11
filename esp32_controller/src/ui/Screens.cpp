#include "audiostreamer/ui/Screens.h"

#include <algorithm>
#include <cstdio>

#include "audiostreamer/display/DisplayFrame.h"

namespace audiostreamer::ui {
namespace {

constexpr const char* kMainMenuItems[] = {
    "Select Sender", "Select Receiver", "Start Streaming", "Settings"};

void renderLines(display::IDisplay& display, const char* const* lines, std::uint8_t count) {
    display::DisplayFrame frame{};
    frame.lineCount = count;
    for (std::uint8_t i = 0; i < count && i < display::DisplayFrame::kMaximumLines; ++i) {
        frame.lines[i] = lines[i];
    }
    display.render(frame);
}

}  // namespace

void BootScreen::enter() {}
void BootScreen::leave() {}
ScreenId BootScreen::handleInput(input::InputEvent) { return ScreenId::Boot; }
void BootScreen::render(display::IDisplay& display) const {
    const char* lines[] = {"AudioStreamer", "Starting..."};
    renderLines(display, lines, 2);
}

ConnectingScreen::ConnectingScreen(UiModel& model) : model_(model) {}
void ConnectingScreen::enter() {}
void ConnectingScreen::leave() {}
ScreenId ConnectingScreen::handleInput(input::InputEvent) { return ScreenId::Connecting; }
void ConnectingScreen::render(display::IDisplay& display) const {
    const char* lines[] = {"AudioStreamer", "WiFi", model_.wifiConnected ? "Connected" : "Connecting..."};
    renderLines(display, lines, 3);
}

DiscoveringScreen::DiscoveringScreen(UiModel& model) : model_(model) {}
void DiscoveringScreen::enter() {}
void DiscoveringScreen::leave() {}
ScreenId DiscoveringScreen::handleInput(input::InputEvent event) {
    return event == input::InputEvent::Pressed ? ScreenId::MainMenu : ScreenId::Discovering;
}
void DiscoveringScreen::render(display::IDisplay& display) const {
    char peersLine[20]{};
    std::snprintf(peersLine, sizeof(peersLine), "Peers: %u", static_cast<unsigned>(model_.peerCount));
    const char* lines[] = {"Control Plane", "Discovering...", peersLine, "Press: menu"};
    renderLines(display, lines, 4);
}

void MainMenuScreen::enter() { menu_.setItems(kMainMenuItems, 4); }
void MainMenuScreen::leave() {}
ScreenId MainMenuScreen::handleInput(input::InputEvent event) {
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) return ScreenId::MainMenu;
    switch (selected) {
        case 0: return ScreenId::SenderSelection;
        case 1: return ScreenId::ReceiverSelection;
        case 2: return ScreenId::Confirm;
        case 3: return ScreenId::Settings;
        default: return ScreenId::MainMenu;
    }
}
void MainMenuScreen::render(display::IDisplay& display) const { menu_.render(display, "Main Menu"); }

PeerSelectionScreen::PeerSelectionScreen(UiModel& model, bool selectSender)
    : model_(model), selectSender_(selectSender) {}
void PeerSelectionScreen::enter() {
    std::size_t count = 0;
    const std::uint16_t requiredCapability = selectSender_ ? 0x0004U : 0x0008U;
    for (std::size_t i = 0; i < model_.peerCount && count < labels_.size(); ++i) {
        if ((model_.peers[i].capabilities & requiredCapability) == 0) continue;
        labels_[count] = model_.peers[i].name;
        peerIndices_[count] = i;
        ++count;
    }
    menu_.setItems(labels_.data(), count);
}
void PeerSelectionScreen::leave() {}
ScreenId PeerSelectionScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) return ScreenId::MainMenu;
    std::size_t selected = 0;
    if (!menu_.handleInput(event, selected)) {
        if (event == input::InputEvent::Pressed && menu_.itemCount() == 0) return ScreenId::MainMenu;
        return selectSender_ ? ScreenId::SenderSelection : ScreenId::ReceiverSelection;
    }
    const UiPeer& peer = model_.peers[peerIndices_[selected]];
    const char* name = peer.name;
    char* destination = selectSender_ ? model_.selectedSender : model_.selectedReceiver;
    std::snprintf(destination, 32, "%s", name);
    if (selectSender_) model_.selectedSenderId = peer.nodeId;
    else model_.selectedReceiverId = peer.nodeId;
    return ScreenId::MainMenu;
}
void PeerSelectionScreen::render(display::IDisplay& display) const {
    menu_.render(display, selectSender_ ? "Select Sender" : "Select Receiver");
}
SenderSelectionScreen::SenderSelectionScreen(UiModel& model) : PeerSelectionScreen(model, true) {}
ReceiverSelectionScreen::ReceiverSelectionScreen(UiModel& model) : PeerSelectionScreen(model, false) {}

ConfirmScreen::ConfirmScreen(UiModel& model) : model_(model) {}
void ConfirmScreen::enter() {}
void ConfirmScreen::leave() {}
ScreenId ConfirmScreen::handleInput(input::InputEvent event) {
    if (event == input::InputEvent::Left) return ScreenId::MainMenu;
    if (event != input::InputEvent::Pressed) return ScreenId::Confirm;
    if (model_.selectedSenderId == 0 || model_.selectedReceiverId == 0) {
        std::snprintf(model_.errorText, sizeof(model_.errorText), "Select sender and receiver first");
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
    const char* lines[] = {"Confirm Route", "From:", model_.selectedSender,
                           "To:", model_.selectedReceiver,
                           model_.errorText[0] == '\0' ? "Press: start" : model_.errorText};
    renderLines(display, lines, 6);
}

StreamingScreen::StreamingScreen(UiModel& model) : model_(model) {}
void StreamingScreen::enter() {}
void StreamingScreen::leave() {}
ScreenId StreamingScreen::handleInput(input::InputEvent event) {
    if (event != input::InputEvent::Pressed && event != input::InputEvent::Left)
        return ScreenId::Streaming;
    if (model_.streamState == StreamUiState::Streaming ||
        model_.streamState == StreamUiState::Waiting) {
        model_.pendingAction = {UiActionKind::StopStream,
                                model_.selectedSenderId, model_.selectedReceiverId};
        model_.streamState = StreamUiState::Waiting;
        model_.errorText[0] = '\0';
        return ScreenId::Streaming;
    }
    return ScreenId::MainMenu;
}
void StreamingScreen::render(display::IDisplay& display) const {
    const char* state = "Stopped";
    if (model_.streamState == StreamUiState::Waiting) state = "Waiting...";
    else if (model_.streamState == StreamUiState::Streaming) state = "Streaming";
    else if (model_.streamState == StreamUiState::Error) state = "Error";
    const char* footer = model_.streamState == StreamUiState::Streaming ? "Press: stop" : "Left: back";
    const char* details = model_.errorText[0] == '\0' ? footer : model_.errorText;
    const char* lines[] = {"Audio Route", state, "From:", model_.selectedSender,
                           "To:", details};
    renderLines(display, lines, 6);
}

void SettingsScreen::enter() {}
void SettingsScreen::leave() {}
ScreenId SettingsScreen::handleInput(input::InputEvent event) {
    return event == input::InputEvent::Left || event == input::InputEvent::Pressed
               ? ScreenId::MainMenu : ScreenId::Settings;
}
void SettingsScreen::render(display::IDisplay& display) const {
    const char* lines[] = {"Settings", "WiFi: WifiSettings.h", "Control port: 55050", "Press: back"};
    renderLines(display, lines, 4);
}

}  // namespace audiostreamer::ui

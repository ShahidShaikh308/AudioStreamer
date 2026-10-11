#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "audiostreamer/ui/Menu.h"
#include "audiostreamer/ui/Screen.h"

namespace audiostreamer::ui {

struct UiPeer final {
    std::uint64_t nodeId = 0;
    std::uint16_t capabilities = 0;
    char name[32]{};
};

enum class StreamUiState : std::uint8_t { Idle, Waiting, Streaming, Error };
enum class UiActionKind : std::uint8_t { None, StartStream, StopStream };

struct UiAction final {
    UiActionKind kind = UiActionKind::None;
    std::uint64_t senderNodeId = 0;
    std::uint64_t receiverNodeId = 0;
};

struct UiModel final {
    static constexpr std::size_t kMaximumPeers = 16;
    std::array<UiPeer, kMaximumPeers> peers{};
    std::size_t peerCount = 0;
    char selectedSender[32] = "None";
    char selectedReceiver[32] = "None";
    std::uint64_t selectedSenderId = 0;
    std::uint64_t selectedReceiverId = 0;
    StreamUiState streamState = StreamUiState::Idle;
    UiAction pendingAction{};
    char errorText[96]{};
    bool wifiConnected = false;
};

class BootScreen final : public Screen {
public:
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
};

class ConnectingScreen final : public Screen {
public:
    explicit ConnectingScreen(UiModel& model);
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
private:
    UiModel& model_;
};

class DiscoveringScreen final : public Screen {
public:
    explicit DiscoveringScreen(UiModel& model);
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
private:
    UiModel& model_;
};

class MainMenuScreen final : public Screen {
public:
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
private:
    Menu menu_;
};

class PeerSelectionScreen : public Screen {
public:
    PeerSelectionScreen(UiModel& model, bool selectSender);
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
protected:
    UiModel& model_;
    bool selectSender_;
    Menu menu_;
    std::array<const char*, UiModel::kMaximumPeers> labels_{};
    std::array<std::size_t, UiModel::kMaximumPeers> peerIndices_{};
};

class SenderSelectionScreen final : public PeerSelectionScreen {
public:
    explicit SenderSelectionScreen(UiModel& model);
};

class ReceiverSelectionScreen final : public PeerSelectionScreen {
public:
    explicit ReceiverSelectionScreen(UiModel& model);
};

class ConfirmScreen final : public Screen {
public:
    explicit ConfirmScreen(UiModel& model);
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
private:
    UiModel& model_;
};

class StreamingScreen final : public Screen {
public:
    explicit StreamingScreen(UiModel& model);
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
private:
    UiModel& model_;
};

class SettingsScreen final : public Screen {
public:
    void enter() override;
    void leave() override;
    ScreenId handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) const override;
};

}  // namespace audiostreamer::ui

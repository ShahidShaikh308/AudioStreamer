#pragma once

#include "audiostreamer/ui/IUi.h"
#include "audiostreamer/ui/Screens.h"

namespace audiostreamer::ui {

class ControllerUi final : public IUi {
public:
    ControllerUi();
    ControllerUi(const ControllerUi&) = delete;
    ControllerUi& operator=(const ControllerUi&) = delete;
    ControllerUi(ControllerUi&&) = delete;
    ControllerUi& operator=(ControllerUi&&) = delete;

    bool initialize() override;
    void handleInput(input::InputEvent event) override;
    void render(display::IDisplay& display) override;
    void updateStatus(bool wifiConnected, bool controlReady, std::uint8_t peerCount) override;
    void setNetworkInfo(const char* localIp, std::uint16_t controlPort, std::uint16_t audioPort);
    void setPeers(const UiPeer* peers, std::size_t count);
    UiAction takeAction();
    void setStreamState(StreamUiState state, const char* errorText = nullptr);
    void setRoutes(const UiRoute* routes, std::size_t count);

private:
    void transition(ScreenId next);
    Screen* screenFor(ScreenId id);

    UiModel model_{};
    BootScreen bootScreen_{};
    ConnectingScreen connectingScreen_;
    DiscoveringScreen discoveringScreen_;
    MainMenuScreen mainMenuScreen_;
    SenderSelectionScreen senderSelectionScreen_;
    ReceiverSelectionScreen receiverSelectionScreen_;
    ConfirmScreen confirmScreen_;
    StreamingScreen streamingScreen_;
    ActiveRoutesScreen activeRoutesScreen_;
    SettingsScreen settingsScreen_;
    DeviceListScreen deviceListScreen_;
    DeviceDetailsScreen deviceDetailsScreen_;
    ScreenId currentId_ = ScreenId::Boot;
    Screen* currentScreen_ = nullptr;
};

}  // namespace audiostreamer::ui

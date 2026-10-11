#pragma once

#include "audiostreamer/config/ControllerConfig.h"
#include "audiostreamer/control_plane/ControlPlaneClient.h"
#include "audiostreamer/display/Ssd1306Display.h"
#include "audiostreamer/input/Ky023Joystick.h"
#include "audiostreamer/network/UdpControlTransport.h"
#include "audiostreamer/network/WifiControlTransport.h"
#include "audiostreamer/ui/ControllerUi.h"

namespace audiostreamer::app {

class ControllerApp final {
public:
    explicit ControllerApp(const config::ControllerConfig& controllerConfig);
    ~ControllerApp();
    ControllerApp(const ControllerApp&) = delete;
    ControllerApp& operator=(const ControllerApp&) = delete;
    ControllerApp(ControllerApp&&) = delete;
    ControllerApp& operator=(ControllerApp&&) = delete;

    bool initialize();
    input::InputState poll();
    bool isInitialized() const;
    void shutdown();

private:
    const config::ControllerConfig& controllerConfig_;
    display::Ssd1306Display display_;
    input::Ky023Joystick input_;
    network::WifiControlTransport wifiConnection_;
    network::UdpControlTransport controlTransport_;
    control_plane::ControlPlaneClient controlPlane_;
    control_plane::ControlPlaneSnapshot controlSnapshot_{};
    ui::ControllerUi ui_;
    std::uint32_t lastDisplayUpdateMs_ = 0;
    bool initialized_ = false;
};

}  // namespace audiostreamer::app

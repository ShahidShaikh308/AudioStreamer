#pragma once

#include "audiostreamer/config/ControllerConfig.h"
#include "audiostreamer/config/PersistentNodeIdentity.h"
#include "audiostreamer/control_plane/ControlPlaneMessageQueue.h"
#include "audiostreamer/control_plane/IControlPlaneClient.h"
#include "audiostreamer/control_plane/NodeRegistry.h"
#include "audiostreamer/network/IControlTransport.h"
#include "audiostreamer/network/IWifiConnection.h"

#include <atomic>

#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <freertos/task.h>

namespace audiostreamer::control_plane {

class ControlPlaneClient final : public IControlPlaneClient {
public:
    ControlPlaneClient() = default;
    ~ControlPlaneClient() override;
    ControlPlaneClient(const ControlPlaneClient&) = delete;
    ControlPlaneClient& operator=(const ControlPlaneClient&) = delete;
    ControlPlaneClient(ControlPlaneClient&&) = delete;
    ControlPlaneClient& operator=(ControlPlaneClient&&) = delete;

    bool initialize(network::IControlTransport& transport,
                    network::IWifiConnection& wifiConnection,
                    const config::ControllerConfig& config) override;
    bool pollSnapshot(ControlPlaneSnapshot& snapshot) override;
    bool queueStartStream(std::uint64_t senderNodeId,
                         std::uint64_t receiverNodeId) override;
    bool queueStopStream(std::uint64_t senderNodeId,
                        std::uint64_t receiverNodeId) override;
    void shutdown() override;

private:
    struct StreamCommand final {
        ControlMessageType type = ControlMessageType::StartStream;
        std::uint64_t senderNodeId = 0;
        std::uint64_t receiverNodeId = 0;
        std::uint16_t transactionId = 0;
    };

    static void taskEntry(void* context);
    void run();
    std::size_t makeHello(std::uint8_t* buffer, std::size_t capacity) const;
    void publish(ControlPlaneState state, bool force = false);
    bool queueCommand(ControlMessageType type, std::uint64_t senderNodeId,
                      std::uint64_t receiverNodeId);

    config::PersistentNodeIdentity identity_;
    NodeRegistry registry_;
    ControlPlaneMessageQueue messageQueue_;
    QueueHandle_t commandQueue_ = nullptr;
    network::IControlTransport* transport_ = nullptr;
    network::IWifiConnection* wifiConnection_ = nullptr;
    const config::ControllerConfig* config_ = nullptr;
    TaskHandle_t task_ = nullptr;
    SemaphoreHandle_t stoppedSemaphore_ = nullptr;
    std::atomic<bool> stopping_{false};
    bool transportReady_ = false;
    ControlPlaneState currentState_ = ControlPlaneState::Stopped;
    std::uint8_t publishedPeerCount_ = 0;
    ControlPlaneSnapshot::StreamState streamState_ = ControlPlaneSnapshot::StreamState::Idle;
    std::uint64_t activeSenderNodeId_ = 0;
    std::uint64_t activeReceiverNodeId_ = 0;
    std::uint16_t activeTransactionId_ = 0;
    std::uint16_t streamErrorCode_ = 0;
    char streamErrorText_[96]{};
    std::atomic<std::uint16_t> nextTransactionId_{1};
};

}  // namespace audiostreamer::control_plane

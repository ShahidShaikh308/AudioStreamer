#include "audiostreamer/control_plane/ControlPlaneClient.h"

#include <Arduino.h>

#include <array>
#include <cstdio>

#include "audiostreamer/control_plane/ControlProtocolV1.h"

namespace audiostreamer::control_plane {
namespace {

constexpr std::uint32_t kHeartbeatIntervalMs = 3000;
constexpr std::uint32_t kHelloIntervalMs = 5000;
constexpr std::uint32_t kPeerTimeoutMs = 10000;
constexpr std::uint32_t kWorkerPeriodMs = 20;
// The worker has fixed packet buffers and nests parser/snapshot frames during receive.
constexpr std::uint32_t kWorkerStackBytes = 8192;
constexpr char kPlatformName[] = "ESP32";
constexpr std::uint8_t kSenderCapability = 0x04;
constexpr std::uint8_t kReceiverCapability = 0x08;

}  // namespace

ControlPlaneClient::~ControlPlaneClient() {
    shutdown();
}

bool ControlPlaneClient::initialize(network::IControlTransport& transport,
                                    network::IWifiConnection& wifiConnection,
                                    const config::ControllerConfig& config) {
    if (task_ != nullptr) return false;
    if (!identity_.begin() || !messageQueue_.begin()) return false;
    commandQueue_ = xQueueCreate(4, sizeof(StreamCommand));
    if (commandQueue_ == nullptr) {
        messageQueue_.shutdown();
        return false;
    }
    stoppedSemaphore_ = xSemaphoreCreateBinary();
    if (stoppedSemaphore_ == nullptr) {
        vQueueDelete(commandQueue_);
        commandQueue_ = nullptr;
        messageQueue_.shutdown();
        return false;
    }
    transport_ = &transport;
    wifiConnection_ = &wifiConnection;
    config_ = &config;
    stopping_.store(false);
    currentState_ = ControlPlaneState::WaitingForWifi;
    publishedPeerCount_ = 0;
    publish(ControlPlaneState::WaitingForWifi, true);
    if (xTaskCreate(taskEntry, "audio_cp", kWorkerStackBytes, this, 2, &task_) != pdPASS) {
        task_ = nullptr;
        vSemaphoreDelete(stoppedSemaphore_);
        stoppedSemaphore_ = nullptr;
        vQueueDelete(commandQueue_);
        commandQueue_ = nullptr;
        messageQueue_.shutdown();
        currentState_ = ControlPlaneState::Error;
        return false;
    }
    return true;
}

bool ControlPlaneClient::pollSnapshot(ControlPlaneSnapshot& snapshot) {
    return messageQueue_.poll(snapshot);
}

bool ControlPlaneClient::queueStartStream(std::uint64_t senderNodeId,
                                          std::uint64_t receiverNodeId) {
    return queueCommand(ControlMessageType::StartStream, senderNodeId, receiverNodeId);
}

bool ControlPlaneClient::queueStopStream(std::uint64_t senderNodeId,
                                         std::uint64_t receiverNodeId) {
    return queueCommand(ControlMessageType::StopStream, senderNodeId, receiverNodeId);
}

bool ControlPlaneClient::queueCommand(ControlMessageType type, std::uint64_t senderNodeId,
                                      std::uint64_t receiverNodeId) {
    if (commandQueue_ == nullptr || senderNodeId == 0 || receiverNodeId == 0 ||
        senderNodeId == receiverNodeId) return false;
    StreamCommand command{};
    command.type = type;
    command.senderNodeId = senderNodeId;
    command.receiverNodeId = receiverNodeId;
    command.transactionId = nextTransactionId_.fetch_add(1);
    if (xQueueSend(commandQueue_, &command, 0) != pdPASS) return false;
    if (task_ != nullptr) xTaskNotifyGive(task_);
    return true;
}

void ControlPlaneClient::shutdown() {
    if (task_ == nullptr) {
        // The worker exclusively owns transport operations. No worker means no open socket.
        transportReady_ = false;
        messageQueue_.shutdown();
        if (stoppedSemaphore_ != nullptr) {
            vSemaphoreDelete(stoppedSemaphore_);
            stoppedSemaphore_ = nullptr;
        }
        if (commandQueue_ != nullptr) {
            vQueueDelete(commandQueue_);
            commandQueue_ = nullptr;
        }
        currentState_ = ControlPlaneState::Stopped;
        return;
    }
    stopping_.store(true);
    xTaskNotifyGive(task_);
    if (stoppedSemaphore_ != nullptr) xSemaphoreTake(stoppedSemaphore_, portMAX_DELAY);
    task_ = nullptr;
    if (stoppedSemaphore_ != nullptr) {
        vSemaphoreDelete(stoppedSemaphore_);
        stoppedSemaphore_ = nullptr;
    }
    messageQueue_.shutdown();
    if (commandQueue_ != nullptr) {
        vQueueDelete(commandQueue_);
        commandQueue_ = nullptr;
    }
    transport_ = nullptr;
    wifiConnection_ = nullptr;
    config_ = nullptr;
    transportReady_ = false;
    currentState_ = ControlPlaneState::Stopped;
}

void ControlPlaneClient::taskEntry(void* context) {
    static_cast<ControlPlaneClient*>(context)->run();
    vTaskDelete(nullptr);
}

std::size_t ControlPlaneClient::makeHello(std::uint8_t* buffer, std::size_t capacity) const {
    return ControlProtocolV1::serializeHello(buffer, capacity, identity_.nodeId(),
                                              config_->nodeName, kPlatformName, 0,
                                              config_->receivePort);
}

void ControlPlaneClient::publish(ControlPlaneState state, bool force) {
    const std::uint8_t count = registry_.size();
    if (!force && state == currentState_ && count == publishedPeerCount_) return;
    currentState_ = state;
    publishedPeerCount_ = count;
    ControlPlaneSnapshot snapshot{};
    snapshot.state = state;
    snapshot.nodeId = identity_.nodeId();
    snapshot.peerCount = count;
    registry_.copyPeers(snapshot.peers, ControlPlaneSnapshot::kMaximumPeers);
    snapshot.streamState = streamState_;
    snapshot.senderNodeId = activeSenderNodeId_;
    snapshot.receiverNodeId = activeReceiverNodeId_;
    snapshot.transactionId = activeTransactionId_;
    snapshot.errorCode = streamErrorCode_;
    std::snprintf(snapshot.errorText, sizeof(snapshot.errorText), "%s", streamErrorText_);
    messageQueue_.publish(snapshot);
}

void ControlPlaneClient::run() {
    std::array<std::uint8_t, ControlProtocolV1::kMaximumDatagramSize> receiveBuffer{};
    std::array<std::uint8_t, ControlProtocolV1::kMaximumDatagramSize> transmitBuffer{};
    std::uint32_t lastHeartbeatMs = 0;
    std::uint32_t lastHelloMs = 0;
    bool helloSent = false;

    while (!stopping_.load()) {
        if (!wifiConnection_->isConnected()) {
            if (transportReady_) {
                transport_->shutdown();
                transportReady_ = false;
            }
            registry_.expire(millis(), kPeerTimeoutMs);
            publish(ControlPlaneState::WaitingForWifi);
            ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(kWorkerPeriodMs));
            continue;
        }
        if (!transportReady_) {
            transportReady_ = transport_->initialize(config_->controlPort);
            if (!transportReady_) {
                publish(ControlPlaneState::Error);
                ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000));
                continue;
            }
            helloSent = false;
            lastHeartbeatMs = 0;
            lastHelloMs = 0;
        }

        const std::uint32_t now = millis();
        if (!helloSent || static_cast<std::uint32_t>(now - lastHelloMs) >= kHelloIntervalMs) {
            const std::size_t size = makeHello(transmitBuffer.data(), transmitBuffer.size());
            if (size > 0) transport_->sendBroadcast(transmitBuffer.data(), size);
            helloSent = true;
            lastHelloMs = now;
        }
        if (lastHeartbeatMs == 0 ||
            static_cast<std::uint32_t>(now - lastHeartbeatMs) >= kHeartbeatIntervalMs) {
            const std::size_t size = ControlProtocolV1::serializeHeartbeat(
                transmitBuffer.data(), transmitBuffer.size(), identity_.nodeId(), PeerStatus::Online);
            if (size > 0) transport_->sendBroadcast(transmitBuffer.data(), size);
            lastHeartbeatMs = now;
        }

        StreamCommand command{};
        while (xQueueReceive(commandQueue_, &command, 0) == pdPASS) {
            PeerRecord sender{};
            PeerRecord receiver{};
            if (!registry_.copyPeer(command.senderNodeId, sender) ||
                !registry_.copyPeer(command.receiverNodeId, receiver)) {
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Selected peer is offline");
                streamErrorCode_ = 1;
                publish(ControlPlaneState::Discovering, true);
                continue;
            }
            if ((sender.capabilities & kSenderCapability) == 0 ||
                (receiver.capabilities & kReceiverCapability) == 0) {
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Peer capability mismatch");
                streamErrorCode_ = 2;
                publish(ControlPlaneState::Discovering, true);
                continue;
            }

            activeSenderNodeId_ = command.senderNodeId;
            activeReceiverNodeId_ = command.receiverNodeId;
            activeTransactionId_ = command.transactionId;
            streamErrorCode_ = 0;
            streamErrorText_[0] = '\0';
            streamState_ = ControlPlaneSnapshot::StreamState::Waiting;
            const std::size_t commandSize = ControlProtocolV1::serializeStreamCommand(
                transmitBuffer.data(), transmitBuffer.size(), command.type,
                command.senderNodeId, command.receiverNodeId, command.transactionId);
            const bool senderSent = commandSize > 0 &&
                transport_->sendTo(transmitBuffer.data(), commandSize, sender.endpoint);
            const bool receiverSent = commandSize > 0 &&
                transport_->sendTo(transmitBuffer.data(), commandSize, receiver.endpoint);
            if (!senderSent || !receiverSent) {
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                streamErrorCode_ = 3;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Control send failed");
            }
            publish(ControlPlaneState::Discovering, true);
        }

        network::Ipv4Endpoint source{};
        bool registryChanged = false;
        for (std::uint8_t drained = 0; drained < 8; ++drained) {
            const std::size_t received = transport_->receive(receiveBuffer.data(), receiveBuffer.size(), source);
            if (received == 0) break;
            ControlMessage message{};
            if (!ControlProtocolV1::parse(receiveBuffer.data(), received, message) ||
                message.nodeId == identity_.nodeId()) continue;

            if (message.type == ControlMessageType::Hello) {
                const bool isNewPeer = !registry_.contains(message.nodeId);
                registryChanged = registry_.upsertHello(message, source, now) || registryChanged;
                if (isNewPeer) {
                    const std::size_t helloSize = makeHello(transmitBuffer.data(), transmitBuffer.size());
                    if (helloSize > 0) transport_->sendTo(transmitBuffer.data(), helloSize, source);
                }
            } else if (message.type == ControlMessageType::Heartbeat) {
                registryChanged = registry_.updateHeartbeat(message.nodeId, message.status,
                                                             source, now) || registryChanged;
            } else if (message.type == ControlMessageType::Goodbye) {
                registryChanged = registry_.remove(message.nodeId) || registryChanged;
            } else if (message.type == ControlMessageType::StreamStatus &&
                       message.senderNodeId == activeSenderNodeId_ &&
                       message.receiverNodeId == activeReceiverNodeId_ &&
                       (!message.hasTransactionId || message.transactionId == activeTransactionId_)) {
                if (message.streamState == audiostreamer::control_plane::StreamState::Running) {
                    streamState_ = ControlPlaneSnapshot::StreamState::Streaming;
                    streamErrorText_[0] = '\0';
                    streamErrorCode_ = 0;
                } else if (message.streamState == audiostreamer::control_plane::StreamState::Stopped) {
                    streamState_ = ControlPlaneSnapshot::StreamState::Idle;
                    streamErrorText_[0] = '\0';
                    streamErrorCode_ = 0;
                } else {
                    streamState_ = ControlPlaneSnapshot::StreamState::Error;
                    streamErrorCode_ = message.errorCode;
                    std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Stream failed (%u)",
                                  static_cast<unsigned>(message.errorCode));
                }
                publish(ControlPlaneState::Discovering, true);
            } else if (message.type == ControlMessageType::Error &&
                       (message.nodeId == activeSenderNodeId_ ||
                        message.nodeId == activeReceiverNodeId_) &&
                       (!message.hasTransactionId || message.transactionId == activeTransactionId_)) {
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                streamErrorCode_ = message.errorCode;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "%s",
                              message.errorText);
                publish(ControlPlaneState::Discovering, true);
            }
        }
        registryChanged = registry_.expire(now, kPeerTimeoutMs) || registryChanged;
        publish(ControlPlaneState::Discovering, registryChanged);
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(kWorkerPeriodMs));
    }

    if (transportReady_) {
        if (wifiConnection_->isConnected()) {
            const std::size_t size = ControlProtocolV1::serializeGoodbye(
                transmitBuffer.data(), transmitBuffer.size(), identity_.nodeId());
            if (size > 0) transport_->sendBroadcast(transmitBuffer.data(), size);
        }
        transport_->shutdown();
        transportReady_ = false;
    }
    publish(ControlPlaneState::Stopped, true);
    if (stoppedSemaphore_ != nullptr) xSemaphoreGive(stoppedSemaphore_);
}

}  // namespace audiostreamer::control_plane

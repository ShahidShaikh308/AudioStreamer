#include "audiostreamer/control_plane/ControlPlaneClient.h"

#include <Arduino.h>

#include <array>
#include <cstdio>
#include <cstring>

#include <esp_system.h>

#include "audiostreamer/control_plane/ControlProtocolV1.h"
#include "audiostreamer/control_plane/ControlProtocolV2.h"

namespace audiostreamer::control_plane {
namespace {

constexpr std::uint32_t kPeerTimeoutMs = 10000;
constexpr std::uint32_t kWorkerPeriodMs = 20;
// The worker has fixed packet buffers and nests parser/snapshot frames during receive.
constexpr std::uint32_t kWorkerStackBytes = 8192;
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
        senderNodeId == receiverNodeId || config_ == nullptr || config_->controllerSecretHex == nullptr ||
        std::strlen(config_->controllerSecretHex) != 64) return false;
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

void ControlPlaneClient::publish(ControlPlaneState state, bool force) {
    const std::uint8_t count = registry_.size();
    if (!force && state == currentState_ && count == publishedPeerCount_ &&
        streamCount_ == publishedStreamCount_) return;
    currentState_ = state;
    publishedPeerCount_ = count;
    publishedStreamCount_ = streamCount_;
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
    snapshot.streamCount = static_cast<std::uint8_t>(streamCount_);
    for (std::size_t i = 0; i < streamCount_; ++i) snapshot.streams[i] = streams_[i];
    messageQueue_.publish(snapshot);
}

void ControlPlaneClient::removeStream(std::size_t index) {
    if (index >= streamCount_) return;
    for (std::size_t i = index + 1; i < streamCount_; ++i) streams_[i - 1] = streams_[i];
    streams_[--streamCount_] = {};
}

bool ControlPlaneClient::removeStreamsForMissingPeers() {
    bool removed = false;
    for (std::size_t i = 0; i < streamCount_;) {
        PeerRecord sender{};
        PeerRecord receiver{};
        if (!registry_.copyPeer(streams_[i].senderNodeId, sender) ||
            !registry_.copyPeer(streams_[i].receiverNodeId, receiver)) {
            removeStream(i);
            removed = true;
        } else {
            ++i;
        }
    }
    return removed;
}

void ControlPlaneClient::run() {
    std::array<std::uint8_t, ControlProtocolV1::kMaximumDatagramSize> receiveBuffer{};
    std::array<std::uint8_t, ControlProtocolV1::kMaximumDatagramSize> transmitBuffer{};

    while (!stopping_.load()) {
        if (!wifiConnection_->isConnected()) {
            if (transportReady_) {
                transport_->shutdown();
                transportReady_ = false;
            }
            registry_.expire(millis(), kPeerTimeoutMs);
            removeStreamsForMissingPeers();
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
        }

        const std::uint32_t now = millis();
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
            std::size_t routeIndex = 0;
            while (routeIndex < streamCount_ &&
                   (streams_[routeIndex].senderNodeId != command.senderNodeId ||
                    streams_[routeIndex].receiverNodeId != command.receiverNodeId)) ++routeIndex;
            if (routeIndex == streamCount_ && streamCount_ < streams_.size()) ++streamCount_;
            if (routeIndex < streams_.size()) {
                streams_[routeIndex].senderNodeId = command.senderNodeId;
                streams_[routeIndex].receiverNodeId = command.receiverNodeId;
                streams_[routeIndex].transactionId = command.transactionId;
                streams_[routeIndex].state = ControlPlaneSnapshot::Stream::State::Waiting;
            }
            streamErrorCode_ = 0;
            streamErrorText_[0] = '\0';
            streamState_ = ControlPlaneSnapshot::StreamState::Waiting;
            std::array<std::uint8_t, ControlProtocolV2::kNonceSize> nonce{};
            bool nonceReady = false;
            for (std::uint8_t& value : nonce) {
                value = static_cast<std::uint8_t>(esp_random());
                nonceReady = nonceReady || value != 0;
            }
            if (!nonceReady) nonce[0] = 1;
            const std::size_t commandSize = ControlProtocolV2::serializeControllerCommand(
                transmitBuffer.data(), transmitBuffer.size(), command.type, identity_.nodeId(),
                command.senderNodeId, command.receiverNodeId, command.transactionId,
                nonce.data(), config_->controllerSecretHex);
            const bool senderSent = commandSize > 0 &&
                transport_->sendTo(transmitBuffer.data(), commandSize, sender.endpoint);
            const bool receiverSent = commandSize > 0 &&
                transport_->sendTo(transmitBuffer.data(), commandSize, receiver.endpoint);
            if (!senderSent || !receiverSent) {
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                streamErrorCode_ = 3;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Control send failed");
                removeStream(routeIndex);
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
                registryChanged = registry_.upsertHello(message, source, now) || registryChanged;
            } else if (message.type == ControlMessageType::Heartbeat) {
                registryChanged = registry_.updateHeartbeat(message.nodeId, message.status,
                                                             source, now) || registryChanged;
            } else if (message.type == ControlMessageType::Goodbye) {
                registryChanged = registry_.remove(message.nodeId) || registryChanged;
                if (registryChanged && removeStreamsForMissingPeers()) publish(ControlPlaneState::Discovering, true);
            } else if (message.type == ControlMessageType::StreamStatus) {
                std::size_t routeIndex = 0;
                while (routeIndex < streamCount_ &&
                       (streams_[routeIndex].senderNodeId != message.senderNodeId ||
                        streams_[routeIndex].receiverNodeId != message.receiverNodeId ||
                        (message.hasTransactionId && streams_[routeIndex].transactionId != message.transactionId))) ++routeIndex;
                if (routeIndex == streamCount_) continue;
                if (message.streamState == audiostreamer::control_plane::StreamState::Running) {
                    streams_[routeIndex].state = ControlPlaneSnapshot::Stream::State::Streaming;
                    streamState_ = ControlPlaneSnapshot::StreamState::Streaming;
                    streamErrorText_[0] = '\0';
                    streamErrorCode_ = 0;
                } else if (message.streamState == audiostreamer::control_plane::StreamState::Stopped) {
                    removeStream(routeIndex);
                    streamState_ = ControlPlaneSnapshot::StreamState::Idle;
                    streamErrorText_[0] = '\0';
                    streamErrorCode_ = 0;
                } else {
                    removeStream(routeIndex);
                    streamState_ = ControlPlaneSnapshot::StreamState::Error;
                    streamErrorCode_ = message.errorCode;
                    std::snprintf(streamErrorText_, sizeof(streamErrorText_), "Stream failed (%u)",
                                  static_cast<unsigned>(message.errorCode));
                }
                publish(ControlPlaneState::Discovering, true);
            } else if (message.type == ControlMessageType::Error) {
                std::size_t routeIndex = 0;
                while (routeIndex < streamCount_ &&
                       ((streams_[routeIndex].senderNodeId != message.nodeId &&
                         streams_[routeIndex].receiverNodeId != message.nodeId) ||
                        (message.hasTransactionId && streams_[routeIndex].transactionId != message.transactionId))) ++routeIndex;
                if (routeIndex == streamCount_) continue;
                removeStream(routeIndex);
                streamState_ = ControlPlaneSnapshot::StreamState::Error;
                streamErrorCode_ = message.errorCode;
                std::snprintf(streamErrorText_, sizeof(streamErrorText_), "%s",
                              message.errorText);
                publish(ControlPlaneState::Discovering, true);
            }
        }
        registryChanged = registry_.expire(now, kPeerTimeoutMs) || registryChanged;
        const bool routesRemoved = removeStreamsForMissingPeers();
        publish(ControlPlaneState::Discovering, registryChanged || routesRemoved);
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(kWorkerPeriodMs));
    }

    if (transportReady_) {
        transport_->shutdown();
        transportReady_ = false;
    }
    publish(ControlPlaneState::Stopped, true);
    if (stoppedSemaphore_ != nullptr) xSemaphoreGive(stoppedSemaphore_);
}

}  // namespace audiostreamer::control_plane

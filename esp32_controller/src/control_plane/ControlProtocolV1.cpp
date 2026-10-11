#include "audiostreamer/control_plane/ControlProtocolV1.h"

#include <cstring>

namespace audiostreamer::control_plane {
namespace {

constexpr std::uint8_t kMagic0 = 'A';
constexpr std::uint8_t kMagic1 = 'C';
constexpr std::uint8_t kVersion = 1;
constexpr std::uint16_t kMaximumCapabilities = 0x000FU;

void writeU16(std::uint8_t* out, std::uint16_t value) {
    out[0] = static_cast<std::uint8_t>(value >> 8U);
    out[1] = static_cast<std::uint8_t>(value);
}

void writeU64(std::uint8_t* out, std::uint64_t value) {
    for (std::size_t i = 0; i < 8; ++i) {
        out[i] = static_cast<std::uint8_t>(value >> (56U - static_cast<unsigned>(i * 8U)));
    }
}

std::uint16_t readU16(const std::uint8_t* in) {
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(in[0]) << 8U) | in[1]);
}

std::uint64_t readU64(const std::uint8_t* in) {
    std::uint64_t value = 0;
    for (std::size_t i = 0; i < 8; ++i) value = (value << 8U) | in[i];
    return value;
}

void writeHeader(std::uint8_t* out, ControlMessageType type, std::uint16_t payloadSize) {
    out[0] = kMagic0;
    out[1] = kMagic1;
    out[2] = kVersion;
    out[3] = static_cast<std::uint8_t>(type);
    writeU16(out + 4, payloadSize);
}

bool validUtf8(const std::uint8_t* data, std::size_t size) {
    std::size_t i = 0;
    while (i < size) {
        const std::uint8_t first = data[i++];
        if (first < 0x80U) continue;
        std::uint32_t codePoint = 0;
        std::size_t continuationCount = 0;
        if ((first & 0xE0U) == 0xC0U) { codePoint = first & 0x1FU; continuationCount = 1; }
        else if ((first & 0xF0U) == 0xE0U) { codePoint = first & 0x0FU; continuationCount = 2; }
        else if ((first & 0xF8U) == 0xF0U) { codePoint = first & 0x07U; continuationCount = 3; }
        else return false;
        if (i + continuationCount > size) return false;
        for (std::size_t j = 0; j < continuationCount; ++j) {
            const std::uint8_t next = data[i++];
            if ((next & 0xC0U) != 0x80U) return false;
            codePoint = (codePoint << 6U) | (next & 0x3FU);
        }
        if ((continuationCount == 1 && codePoint < 0x80U) ||
            (continuationCount == 2 && codePoint < 0x800U) ||
            (continuationCount == 3 && codePoint < 0x10000U) ||
            codePoint > 0x10FFFFU || (codePoint >= 0xD800U && codePoint <= 0xDFFFU)) return false;
    }
    return true;
}

bool copyText(const std::uint8_t* data, std::size_t size, char* output) {
    if (size > 255 || !validUtf8(data, size)) return false;
    if (size > 0) std::memcpy(output, data, size);
    output[size] = '\0';
    return true;
}

}  // namespace

std::size_t ControlProtocolV1::serializeHello(std::uint8_t* output, std::size_t capacity,
                                               std::uint64_t nodeId, const char* name,
                                               const char* platform, std::uint16_t capabilities,
                                               std::uint16_t receivePort) {
    if (output == nullptr || name == nullptr || platform == nullptr || nodeId == 0 ||
        receivePort == 0 || (capabilities & ~kMaximumCapabilities) != 0) return 0;
    const std::size_t nameSize = std::strlen(name);
    const std::size_t platformSize = std::strlen(platform);
    const std::size_t payloadSize = 8 + 1 + nameSize + 1 + platformSize + 2 + 2;
    const std::size_t totalSize = kHeaderSize + payloadSize;
    if (nameSize == 0 || nameSize > 255 || platformSize == 0 || platformSize > 255 ||
        totalSize > capacity || totalSize > kMaximumDatagramSize) return 0;

    writeHeader(output, ControlMessageType::Hello, static_cast<std::uint16_t>(payloadSize));
    std::uint8_t* payload = output + kHeaderSize;
    writeU64(payload, nodeId);
    std::size_t offset = 8;
    payload[offset++] = static_cast<std::uint8_t>(nameSize);
    std::memcpy(payload + offset, name, nameSize);
    offset += nameSize;
    payload[offset++] = static_cast<std::uint8_t>(platformSize);
    std::memcpy(payload + offset, platform, platformSize);
    offset += platformSize;
    writeU16(payload + offset, capabilities);
    writeU16(payload + offset + 2, receivePort);
    return totalSize;
}

std::size_t ControlProtocolV1::serializeHeartbeat(std::uint8_t* output, std::size_t capacity,
                                                   std::uint64_t nodeId, PeerStatus status) {
    if (output == nullptr || capacity < kHeaderSize + 9 || nodeId == 0 ||
        status < PeerStatus::Online || status > PeerStatus::Error) return 0;
    writeHeader(output, ControlMessageType::Heartbeat, 9);
    writeU64(output + kHeaderSize, nodeId);
    output[kHeaderSize + 8] = static_cast<std::uint8_t>(status);
    return kHeaderSize + 9;
}

std::size_t ControlProtocolV1::serializeGoodbye(std::uint8_t* output, std::size_t capacity,
                                                 std::uint64_t nodeId) {
    if (output == nullptr || capacity < kHeaderSize + 8 || nodeId == 0) return 0;
    writeHeader(output, ControlMessageType::Goodbye, 8);
    writeU64(output + kHeaderSize, nodeId);
    return kHeaderSize + 8;
}

std::size_t ControlProtocolV1::serializeStreamCommand(std::uint8_t* output,
                                                       std::size_t capacity,
                                                       ControlMessageType type,
                                                       std::uint64_t senderNodeId,
                                                       std::uint64_t receiverNodeId,
                                                       std::uint16_t transactionId) {
    constexpr std::size_t kPayloadSize = 18;
    if (output == nullptr || capacity < kHeaderSize + kPayloadSize ||
        (type != ControlMessageType::StartStream && type != ControlMessageType::StopStream) ||
        senderNodeId == 0 || receiverNodeId == 0 || senderNodeId == receiverNodeId) return 0;
    writeHeader(output, type, kPayloadSize);
    std::uint8_t* payload = output + kHeaderSize;
    writeU64(payload, senderNodeId);
    writeU64(payload + 8, receiverNodeId);
    writeU16(payload + 16, transactionId);
    return kHeaderSize + kPayloadSize;
}

bool ControlProtocolV1::parse(const std::uint8_t* data, std::size_t size,
                              ControlMessage& message) {
    if (data == nullptr || size < kHeaderSize || size > kMaximumDatagramSize ||
        data[0] != kMagic0 || data[1] != kMagic1 || data[2] != kVersion ||
        readU16(data + 4) != size - kHeaderSize) return false;
    const std::uint8_t rawType = data[3];
    if (rawType < static_cast<std::uint8_t>(ControlMessageType::Hello) ||
        rawType > static_cast<std::uint8_t>(ControlMessageType::Error)) return false;

    ControlMessage parsed{};
    parsed.type = static_cast<ControlMessageType>(rawType);
    const std::uint8_t* payload = data + kHeaderSize;
    const std::size_t payloadSize = size - kHeaderSize;
    if (parsed.type == ControlMessageType::Goodbye) {
        if (payloadSize < 8) return false;
        parsed.nodeId = readU64(payload);
    } else if (parsed.type == ControlMessageType::Heartbeat) {
        if (payloadSize < 9) return false;
        parsed.nodeId = readU64(payload);
        if (payload[8] < static_cast<std::uint8_t>(PeerStatus::Online) ||
            payload[8] > static_cast<std::uint8_t>(PeerStatus::Error)) return false;
        parsed.status = static_cast<PeerStatus>(payload[8]);
    } else if (parsed.type == ControlMessageType::Hello) {
        if (payloadSize < 12) return false;
        parsed.nodeId = readU64(payload);
        std::size_t offset = 8;
        const std::size_t nameSize = payload[offset++];
        if (nameSize == 0 || nameSize > payloadSize - offset ||
            !copyText(payload + offset, nameSize, parsed.name)) return false;
        offset += nameSize;
        if (offset >= payloadSize) return false;
        const std::size_t platformSize = payload[offset++];
        if (platformSize == 0 || platformSize > payloadSize - offset ||
            !copyText(payload + offset, platformSize, parsed.platform)) return false;
        offset += platformSize;
        if (payloadSize - offset < 4) return false;
        parsed.capabilities = readU16(payload + offset);
        parsed.receivePort = readU16(payload + offset + 2);
        if ((parsed.capabilities & ~kMaximumCapabilities) != 0 || parsed.receivePort == 0) return false;
    } else if (parsed.type == ControlMessageType::StartStream ||
               parsed.type == ControlMessageType::StopStream) {
        if (payloadSize < 16) return false;
        parsed.senderNodeId = readU64(payload);
        parsed.receiverNodeId = readU64(payload + 8);
        if (payloadSize - 16 == 1) return false;
        if (payloadSize - 16 >= 2) {
            parsed.transactionId = readU16(payload + 16);
            parsed.hasTransactionId = true;
        }
        if (parsed.senderNodeId == 0 || parsed.receiverNodeId == 0 ||
            parsed.senderNodeId == parsed.receiverNodeId) return false;
    } else if (parsed.type == ControlMessageType::StreamStatus) {
        if (payloadSize < 19) return false;
        parsed.senderNodeId = readU64(payload);
        parsed.receiverNodeId = readU64(payload + 8);
        if (payload[16] < static_cast<std::uint8_t>(StreamState::Running) ||
            payload[16] > static_cast<std::uint8_t>(StreamState::Failed)) return false;
        parsed.streamState = static_cast<StreamState>(payload[16]);
        parsed.errorCode = readU16(payload + 17);
        if (payloadSize - 19 == 1) return false;
        if (payloadSize - 19 >= 2) {
            parsed.transactionId = readU16(payload + 19);
            parsed.hasTransactionId = true;
        }
    } else {
        if (payloadSize < 11) return false;
        parsed.nodeId = readU64(payload);
        parsed.errorCode = readU16(payload + 8);
        const std::size_t textSize = payload[10];
        if (textSize > payloadSize - 11 || !copyText(payload + 11, textSize, parsed.errorText)) return false;
        const std::size_t trailing = payloadSize - 11 - textSize;
        if (trailing == 1) return false;
        if (trailing >= 2) {
            parsed.transactionId = readU16(payload + 11 + textSize);
            parsed.hasTransactionId = true;
        }
    }
    if ((parsed.type == ControlMessageType::Hello ||
         parsed.type == ControlMessageType::Goodbye ||
         parsed.type == ControlMessageType::Heartbeat ||
         parsed.type == ControlMessageType::Error) && parsed.nodeId == 0) return false;
    message = parsed;
    return true;
}

}  // namespace audiostreamer::control_plane

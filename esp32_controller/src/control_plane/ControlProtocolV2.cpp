#include "audiostreamer/control_plane/ControlProtocolV2.h"

#include <mbedtls/md.h>

#include <cstring>

namespace audiostreamer::control_plane {
namespace {

constexpr std::uint8_t kMagic0 = 'A';
constexpr std::uint8_t kMagic1 = 'C';
constexpr std::uint8_t kVersion = 2;
constexpr std::size_t kSecretHexSize = 64;
constexpr std::size_t kAuthenticatedPrefixSize = ControlProtocolV2::kAuthenticatedCommandSize - ControlProtocolV2::kTagSize;

int hexValue(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

void writeU16(std::uint8_t* output, std::uint16_t value) {
    output[0] = static_cast<std::uint8_t>(value >> 8U);
    output[1] = static_cast<std::uint8_t>(value);
}

void writeU64(std::uint8_t* output, std::uint64_t value) {
    for (std::size_t i = 0; i < 8; ++i)
        output[i] = static_cast<std::uint8_t>(value >> (56U - static_cast<unsigned>(i * 8U)));
}

}  // namespace

std::size_t ControlProtocolV2::serializeControllerCommand(std::uint8_t* output, std::size_t capacity,
    ControlMessageType type, std::uint64_t controllerNodeId, std::uint64_t senderNodeId,
    std::uint64_t receiverNodeId, std::uint16_t transactionId,
    const std::uint8_t nonce[kNonceSize], const char* secretHex) {
    if (output == nullptr || capacity < kAuthenticatedCommandSize ||
        (type != ControlMessageType::StartStream && type != ControlMessageType::StopStream) ||
        controllerNodeId == 0 || senderNodeId == 0 || receiverNodeId == 0 ||
        senderNodeId == receiverNodeId || nonce == nullptr || secretHex == nullptr ||
        std::strlen(secretHex) != kSecretHexSize) return 0;

    std::uint8_t secret[32]{};
    for (std::size_t i = 0; i < sizeof(secret); ++i) {
        const int high = hexValue(secretHex[i * 2]);
        const int low = hexValue(secretHex[i * 2 + 1]);
        if (high < 0 || low < 0) return 0;
        secret[i] = static_cast<std::uint8_t>((high << 4) | low);
    }
    bool nonceHasValue = false;
    for (std::size_t i = 0; i < kNonceSize; ++i) nonceHasValue = nonceHasValue || nonce[i] != 0;
    if (!nonceHasValue) return 0;

    output[0] = kMagic0;
    output[1] = kMagic1;
    output[2] = kVersion;
    output[3] = static_cast<std::uint8_t>(type);
    writeU16(output + 4, static_cast<std::uint16_t>(kAuthenticatedCommandSize - ControlProtocolV1::kHeaderSize));
    std::size_t offset = ControlProtocolV1::kHeaderSize;
    writeU64(output + offset, controllerNodeId); offset += 8;
    writeU64(output + offset, senderNodeId); offset += 8;
    writeU64(output + offset, receiverNodeId); offset += 8;
    writeU16(output + offset, transactionId); offset += 2;
    std::memcpy(output + offset, nonce, kNonceSize);

    const mbedtls_md_info_t* info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    const int result = info == nullptr ? -1 : mbedtls_md_hmac(info, secret, sizeof(secret),
        output, kAuthenticatedPrefixSize, output + kAuthenticatedPrefixSize);
    std::memset(secret, 0, sizeof(secret));
    return result == 0 ? kAuthenticatedCommandSize : 0;
}

}  // namespace audiostreamer::control_plane

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AudioStreamer.Control.Protocol;

/// <summary>A controller-originated stream command authenticated with the shared controller key.</summary>
public sealed record AuthenticatedControllerCommandMessage(
    bool IsStart,
    ulong ControllerNodeId,
    ulong SenderNodeId,
    ulong ReceiverNodeId,
    ushort TransactionId,
    byte[] Nonce) : ControlMessage;

/// <summary>
/// Control Protocol v2 command envelope. Discovery and node-to-node messages remain v1.
/// The HMAC covers the complete header and payload prefix, including the command and nonce.
/// </summary>
public static class AuthenticatedControllerCommandCodec
{
    public const byte Version = 2;
    public const int SecretLength = 32;
    public const int NonceLength = 16;
    public const int TagLength = 32;
    private const int HeaderLength = 6;
    private const int AuthenticatedPayloadLength = 8 + 8 + 8 + 2 + NonceLength;
    private const int TotalLength = HeaderLength + AuthenticatedPayloadLength + TagLength;

    public static byte[] Serialize(AuthenticatedControllerCommandMessage message, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateSecret(secret);
        ValidateMessage(message);

        var packet = new byte[TotalLength];
        packet[0] = 0x41;
        packet[1] = 0x43;
        packet[2] = Version;
        packet[3] = message.IsStart ? (byte)ControlMessageType.StartStream : (byte)ControlMessageType.StopStream;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(TotalLength - HeaderLength));
        var offset = HeaderLength;
        WriteUInt64(message.ControllerNodeId);
        WriteUInt64(message.SenderNodeId);
        WriteUInt64(message.ReceiverNodeId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), message.TransactionId);
        offset += sizeof(ushort);
        message.Nonce.CopyTo(packet, offset);
        offset += NonceLength;
        HMACSHA256.HashData(secret, packet.AsSpan(0, offset), packet.AsSpan(offset, TagLength));
        return packet;

        void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(offset), value);
            offset += sizeof(ulong);
        }
    }

    public static AuthenticatedControllerCommandMessage ParseAndVerify(ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> secret)
    {
        ValidateSecret(secret);
        if (packet.Length != TotalLength || packet[0] != 0x41 || packet[1] != 0x43 ||
            packet[2] != Version || packet[3] is not ((byte)ControlMessageType.StartStream) and
                not ((byte)ControlMessageType.StopStream) ||
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != packet.Length - HeaderLength)
            throw new FormatException("Authenticated controller command header is invalid.");

        var tagOffset = packet.Length - TagLength;
        Span<byte> expectedTag = stackalloc byte[TagLength];
        HMACSHA256.HashData(secret, packet[..tagOffset], expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, packet[tagOffset..]))
            throw new FormatException("Controller command authentication failed.");

        var payload = packet[HeaderLength..tagOffset];
        var message = new AuthenticatedControllerCommandMessage(
            packet[3] == (byte)ControlMessageType.StartStream,
            BinaryPrimitives.ReadUInt64BigEndian(payload),
            BinaryPrimitives.ReadUInt64BigEndian(payload[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(payload[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[24..]),
            payload.Slice(26, NonceLength).ToArray());
        ValidateMessage(message);
        return message;
    }

    public static byte[] ParseSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != SecretLength * 2)
            throw new FormatException($"Controller secret must be exactly {SecretLength * 2} hexadecimal characters.");
        try { return Convert.FromHexString(value); }
        catch (FormatException exception) { throw new FormatException("Controller secret must contain only hexadecimal characters.", exception); }
    }

    private static void ValidateSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretLength) throw new ArgumentException($"Controller secret must be {SecretLength} bytes.", nameof(secret));
    }

    private static void ValidateMessage(AuthenticatedControllerCommandMessage message)
    {
        if (message.ControllerNodeId == 0 || message.SenderNodeId == 0 || message.ReceiverNodeId == 0 ||
            message.SenderNodeId == message.ReceiverNodeId || message.Nonce is not { Length: NonceLength } ||
            message.Nonce.All(static value => value == 0))
            throw new ArgumentException("Authenticated controller command fields are invalid.", nameof(message));
    }
}

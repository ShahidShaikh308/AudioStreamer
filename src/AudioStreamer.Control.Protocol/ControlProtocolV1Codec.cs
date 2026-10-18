using System.Buffers.Binary;
using System.Text;

namespace AudioStreamer.Control.Protocol;

public enum ControlMessageType : byte { Hello = 1, Goodbye = 2, Heartbeat = 3, StartStream = 4, StopStream = 5, StreamStatus = 6, Error = 7 }
public enum ControlNodeStatus : byte { Offline = 0, Online = 1, Busy = 2, Streaming = 3, Error = 4 }
public enum ControlStreamState : byte { Running = 1, Stopped = 2, Failed = 3 }
[Flags] public enum ControlCapability : ushort { Capture = 1, Playback = 2, Sender = 4, Receiver = 8 }

public abstract record ControlMessage;
public sealed record HelloMessage(ulong NodeId, string Name, string Platform, ControlCapability Capabilities, ushort ReceivePort) : ControlMessage;
public sealed record GoodbyeMessage(ulong NodeId) : ControlMessage;
public sealed record HeartbeatMessage(ulong NodeId, ControlNodeStatus Status) : ControlMessage;
public sealed record StreamCommandMessage(bool IsStart, ulong SenderNodeId, ulong ReceiverNodeId, ushort? TransactionId = null) : ControlMessage;
public sealed record StreamStatusMessage(ulong SenderNodeId, ulong ReceiverNodeId, ControlStreamState State, ushort ErrorCode = 0, ushort? TransactionId = null) : ControlMessage;
public sealed record ErrorMessage(ulong NodeId, ushort ErrorCode, string Text, ushort? TransactionId = null) : ControlMessage;

/// <summary>Control Protocol v1 wire codec. Layout and validation follow docs/CONTROL_PROTOCOL.md.</summary>
public static class ControlProtocolV1Codec
{
    private const int HeaderLength = 6;
    public const int MaximumDatagramLength = 1200;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Serialize(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = new byte[MaximumDatagramLength - HeaderLength];
        var offset = 0;
        ControlMessageType type;
        switch (message)
        {
            case HelloMessage hello:
                type = ControlMessageType.Hello;
                WriteU64(hello.NodeId); WriteString(hello.Name); WriteString(hello.Platform);
                if ((hello.Capabilities & ~(ControlCapability)0x000f) != 0 || hello.ReceivePort == 0) throw new ArgumentException("HELLO capabilities or receive port are invalid.");
                WriteU16((ushort)hello.Capabilities); WriteU16(hello.ReceivePort);
                break;
            case GoodbyeMessage goodbye:
                type = ControlMessageType.Goodbye; WriteU64(goodbye.NodeId); break;
            case HeartbeatMessage heartbeat:
                type = ControlMessageType.Heartbeat;
                if (!Enum.IsDefined(heartbeat.Status) || heartbeat.Status == ControlNodeStatus.Offline) throw new ArgumentException("HEARTBEAT status is invalid.");
                WriteU64(heartbeat.NodeId); payload[offset++] = (byte)heartbeat.Status; break;
            case StreamCommandMessage command:
                type = command.IsStart ? ControlMessageType.StartStream : ControlMessageType.StopStream;
                ValidatePair(command.SenderNodeId, command.ReceiverNodeId);
                WriteU64(command.SenderNodeId); WriteU64(command.ReceiverNodeId); WriteTransaction(command.TransactionId); break;
            case StreamStatusMessage status:
                type = ControlMessageType.StreamStatus;
                ValidatePair(status.SenderNodeId, status.ReceiverNodeId);
                if (!Enum.IsDefined(status.State)) throw new ArgumentException("Stream state is invalid.");
                WriteU64(status.SenderNodeId); WriteU64(status.ReceiverNodeId); payload[offset++] = (byte)status.State;
                WriteU16(status.ErrorCode); WriteTransaction(status.TransactionId); break;
            case ErrorMessage error:
                type = ControlMessageType.Error; WriteU64(error.NodeId); WriteU16(error.ErrorCode); WriteString(error.Text, true); WriteTransaction(error.TransactionId); break;
            default: throw new ArgumentException("Unsupported Control Protocol message.", nameof(message));
        }
        if (offset + HeaderLength > MaximumDatagramLength) throw new ArgumentException("Control Protocol datagram exceeds its maximum size.");
        var result = new byte[offset + HeaderLength];
        result[0] = 0x41; result[1] = 0x43; result[2] = 1; result[3] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)offset);
        payload.AsSpan(0, offset).CopyTo(result.AsSpan(HeaderLength));
        return result;

        void WriteU16(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), value); offset += 2; }
        void WriteU64(ulong value) { if (value == 0) throw new ArgumentException("Node IDs must be nonzero."); BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset), value); offset += 8; }
        void WriteString(string value, bool allowEmpty = false)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            if ((!allowEmpty && bytes.Length == 0) || bytes.Length > byte.MaxValue || value.Contains('\0')) throw new ArgumentException("Control Protocol string length or contents are invalid.");
            payload[offset++] = (byte)bytes.Length; bytes.CopyTo(payload, offset); offset += bytes.Length;
        }
        void WriteTransaction(ushort? id) { if (id.HasValue) WriteU16(id.Value); }
    }

    public static ControlMessage Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is < HeaderLength or > MaximumDatagramLength || datagram[0] != 0x41 || datagram[1] != 0x43 || datagram[2] != 1)
            throw new FormatException("Control Protocol header is invalid.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]);
        if (length != datagram.Length - HeaderLength) throw new FormatException("Control Protocol payload length is inconsistent.");
        var reader = new PayloadReader(datagram[HeaderLength..]);
        ControlMessage parsed = (ControlMessageType)datagram[3] switch
        {
            ControlMessageType.Hello => ParseHello(ref reader),
            ControlMessageType.Goodbye => new GoodbyeMessage(reader.ReadU64()),
            ControlMessageType.Heartbeat => ParseHeartbeat(ref reader),
            ControlMessageType.StartStream => ParseCommand(ref reader, true),
            ControlMessageType.StopStream => ParseCommand(ref reader, false),
            ControlMessageType.StreamStatus => ParseStreamStatus(ref reader),
            ControlMessageType.Error => ParseError(ref reader),
            _ => throw new FormatException("Unknown Control Protocol message type.")
        };
        if (reader.Remaining != 0 && parsed is not (StreamCommandMessage or StreamStatusMessage or ErrorMessage)) throw new FormatException("Unexpected trailing Control Protocol data.");
        return parsed;
    }

    private static ControlMessage ParseHello(ref PayloadReader reader)
    {
        var id = reader.ReadU64(); var name = reader.ReadString(); var platform = reader.ReadString();
        var capabilities = reader.ReadU16(); var port = reader.ReadU16();
        if ((capabilities & ~0x000f) != 0 || port == 0) throw new FormatException("HELLO fields are invalid.");
        return new HelloMessage(id, name, platform, (ControlCapability)capabilities, port);
    }

    private static ControlMessage ParseHeartbeat(ref PayloadReader reader)
    {
        var id = reader.ReadU64(); var status = (ControlNodeStatus)reader.ReadByte();
        if (!Enum.IsDefined(status) || status == ControlNodeStatus.Offline) throw new FormatException("HEARTBEAT status is invalid.");
        return new HeartbeatMessage(id, status);
    }

    private static ControlMessage ParseCommand(ref PayloadReader reader, bool start)
    {
        var sender = reader.ReadU64(); var receiver = reader.ReadU64();
        if (sender == receiver) throw new FormatException("Stream endpoint IDs must be distinct.");
        return new StreamCommandMessage(start, sender, receiver, reader.ReadTransaction());
    }

    private static ControlMessage ParseStreamStatus(ref PayloadReader reader)
    {
        var sender = reader.ReadU64(); var receiver = reader.ReadU64();
        if (sender == receiver) throw new FormatException("Stream endpoint IDs must be distinct.");
        var state = (ControlStreamState)reader.ReadByte(); var code = reader.ReadU16();
        if (!Enum.IsDefined(state)) throw new FormatException("Stream state is invalid.");
        return new StreamStatusMessage(sender, receiver, state, code, reader.ReadTransaction());
    }

    private static ControlMessage ParseError(ref PayloadReader reader) =>
        new ErrorMessage(reader.ReadU64(), reader.ReadU16(), reader.ReadString(allowEmpty: true), reader.ReadTransaction());

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public PayloadReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }
        public int Remaining => _data.Length - _offset;
        public byte ReadByte() { Require(1); return _data[_offset++]; }
        public ushort ReadU16() { Require(2); var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_offset..]); _offset += 2; return value; }
        public ulong ReadU64() { Require(8); var value = BinaryPrimitives.ReadUInt64BigEndian(_data[_offset..]); _offset += 8; if (value == 0) throw new FormatException("Node ID must be nonzero."); return value; }
        public string ReadString(bool allowEmpty = false)
        {
            var size = ReadByte(); Require(size);
            string value;
            try { value = StrictUtf8.GetString(_data.Slice(_offset, size)); }
            catch (DecoderFallbackException exception) { throw new FormatException("Control Protocol string is not valid UTF-8.", exception); }
            _offset += size;
            if ((!allowEmpty && value.Length == 0) || value.Contains('\0')) throw new FormatException("Control Protocol string is invalid.");
            return value;
        }
        public ushort? ReadTransaction() => Remaining switch { 0 => null, 1 => throw new FormatException("Transaction ID is truncated."), _ => ReadU16() };
        private void Require(int count) { if (count > Remaining) throw new FormatException("Control Protocol payload is truncated."); }
    }

    private static void ValidatePair(ulong sender, ulong receiver)
    {
        if (sender == 0 || receiver == 0 || sender == receiver) throw new ArgumentException("Stream endpoint IDs must be distinct and nonzero.");
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AudioStreamer.Engine.Contracts;

/// <summary>Stable mapping between Engine GUIDs and the frozen Control v1 u64 node IDs.</summary>
public static class ControlNodeIdentity
{
    private const ulong PeerMarker = 0x415544494F563231;

    public static ulong ToProtocolId(Guid engineId)
    {
        if (engineId == Guid.Empty) throw new ArgumentException("Engine node ID must be non-empty.", nameof(engineId));
        Span<byte> bytes = stackalloc byte[16];
        engineId.TryWriteBytes(bytes);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]) == PeerMarker)
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        var id = BinaryPrimitives.ReadUInt64BigEndian(hash);
        return id == 0 ? 1 : id;
    }

    public static Guid ToEnginePeerId(ulong protocolId)
    {
        if (protocolId == 0) throw new ArgumentOutOfRangeException(nameof(protocolId));
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, protocolId);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], PeerMarker);
        return new Guid(bytes);
    }
}

using AudioStreamer.Platform.Windows;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Service;

/// <summary>Preserves the historical 64-bit Control v1 node identity inside its reversible Engine GUID marker.</summary>
public sealed class LegacyNodeIdentityMapper : ILegacyNodeIdentityMapper
{
    public Guid ToEngineNodeId(ulong legacyProtocolNodeId) => ControlNodeIdentity.ToEnginePeerId(legacyProtocolNodeId);
}

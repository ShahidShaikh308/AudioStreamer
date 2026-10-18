using AudioStreamer.Engine.Contracts;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class ControlNodeIdentityTests
{
    [Fact]
    public void EnginePeerIdentityRoundTripsProtocolIdentityAndEngineIdentityIsStable()
    {
        const ulong protocolId = 0x0102030405060708;
        var enginePeerId = ControlNodeIdentity.ToEnginePeerId(protocolId);
        Assert.Equal(protocolId, ControlNodeIdentity.ToProtocolId(enginePeerId));
        var localEngineId = Guid.Parse("9ef78655-1f71-48f8-b625-f1745cc86f21");
        Assert.Equal(ControlNodeIdentity.ToProtocolId(localEngineId), ControlNodeIdentity.ToProtocolId(localEngineId));
    }
}

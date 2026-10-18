using AudioStreamer.Control.Protocol;
using Xunit;

namespace AudioStreamer.Control.Protocol.Tests;

public sealed class ControlProtocolV1Tests
{
    [Fact]
    public void HelloMatchesDocumentedBigEndianWireLayout()
    {
        var message = new HelloMessage(0x0102030405060708, "Desk", "windows",
            ControlCapability.Capture | ControlCapability.Playback | ControlCapability.Sender | ControlCapability.Receiver, 5006);
        var packet = ControlProtocolV1Codec.Serialize(message);
        Assert.Equal("4143010100190102030405060708044465736b0777696e646f7773000f138e", Convert.ToHexString(packet).ToLowerInvariant());
        Assert.Equal(message, ControlProtocolV1Codec.Parse(packet));
    }

    [Fact]
    public void RejectsTruncatedTransactionId()
    {
        var packet = ControlProtocolV1Codec.Serialize(new StreamCommandMessage(true, 11, 22, 0x1234));
        var truncated = packet[..^1];
        truncated[4] = 0;
        truncated[5] = 17;
        Assert.Throws<FormatException>(() => ControlProtocolV1Codec.Parse(truncated));
    }

    [Fact]
    public void CoreMessageTypesMatchPythonReferenceBytes()
    {
        ControlMessage[] messages =
        [
            new GoodbyeMessage(0x0102030405060708),
            new HeartbeatMessage(0x0102030405060708, ControlNodeStatus.Streaming),
            new StreamCommandMessage(true, 11, 22, 0x1234),
            new StreamCommandMessage(false, 11, 22),
            new StreamStatusMessage(11, 22, ControlStreamState.Running, 0x0102, 0x3344),
            new ErrorMessage(7, 0x0203, "bad", 0x1234)
        ];
        string[] referenceHex =
        [
            "4143010200080102030405060708",
            "414301030009010203040506070803",
            "414301040012000000000000000b00000000000000161234",
            "414301050010000000000000000b0000000000000016",
            "414301060015000000000000000b00000000000000160101023344",
            "41430107001000000000000000070203036261641234"
        ];
        for (var index = 0; index < messages.Length; index++)
        {
            var bytes = ControlProtocolV1Codec.Serialize(messages[index]);
            Assert.Equal(referenceHex[index], Convert.ToHexString(bytes).ToLowerInvariant());
            Assert.Equal(messages[index], ControlProtocolV1Codec.Parse(bytes));
        }
    }

    [Theory]
    [InlineData("000301010000")]
    [InlineData("414302010000")]
    [InlineData("414301630000")]
    public void RejectsInvalidHeaderOrMessageType(string hex)
    {
        Assert.Throws<FormatException>(() => ControlProtocolV1Codec.Parse(Convert.FromHexString(hex)));
    }
}

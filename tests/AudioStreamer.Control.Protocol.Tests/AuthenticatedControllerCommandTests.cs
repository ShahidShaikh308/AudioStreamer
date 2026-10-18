using AudioStreamer.Control.Protocol;
using Xunit;

namespace AudioStreamer.Control.Protocol.Tests;

public sealed class AuthenticatedControllerCommandTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthenticatedCommandRoundTripsAndUsesVersionTwo(bool isStart)
    {
        var message = new AuthenticatedControllerCommandMessage(isStart, 33, 11, 22, 0x1234, Nonce);
        var bytes = AuthenticatedControllerCommandCodec.Serialize(message, Secret);

        Assert.Equal(2, bytes[2]);
        Assert.Equal(isStart ? (byte)4 : (byte)5, bytes[3]);
        var parsed = AuthenticatedControllerCommandCodec.ParseAndVerify(bytes, Secret);
        Assert.Equal(message.IsStart, parsed.IsStart);
        Assert.Equal(message.ControllerNodeId, parsed.ControllerNodeId);
        Assert.Equal(message.SenderNodeId, parsed.SenderNodeId);
        Assert.Equal(message.ReceiverNodeId, parsed.ReceiverNodeId);
        Assert.Equal(message.TransactionId, parsed.TransactionId);
        Assert.Equal(message.Nonce, parsed.Nonce);
    }

    [Fact]
    public void StartCommandMatchesSharedCrossPlatformHmacVector()
    {
        var packet = AuthenticatedControllerCommandCodec.Serialize(
            new AuthenticatedControllerCommandMessage(true, 33, 11, 22, 0x1234, Nonce), Secret);
        Assert.Equal("41430204004a0000000000000021000000000000000b000000000000001612340102030405060708090a0b0c0d0e0f1082f2ab57ecbc3961153279f7fe533ad392f5c0a06b949a91a183a2154227f154",
            Convert.ToHexString(packet).ToLowerInvariant());
    }

    [Fact]
    public void RejectsWrongSecretAndTamperedPayload()
    {
        var bytes = AuthenticatedControllerCommandCodec.Serialize(
            new AuthenticatedControllerCommandMessage(true, 33, 11, 22, 9, Nonce), Secret);
        var wrongSecret = Secret.ToArray();
        wrongSecret[0] ^= 1;
        Assert.Throws<FormatException>(() => AuthenticatedControllerCommandCodec.ParseAndVerify(bytes, wrongSecret));

        bytes[20] ^= 1;
        Assert.Throws<FormatException>(() => AuthenticatedControllerCommandCodec.ParseAndVerify(bytes, Secret));
    }

    [Fact]
    public void RejectsV1AndMalformedCommands()
    {
        var bytes = AuthenticatedControllerCommandCodec.Serialize(
            new AuthenticatedControllerCommandMessage(true, 33, 11, 22, 9, Nonce), Secret);
        Assert.Throws<FormatException>(() => ControlProtocolV1Codec.Parse(bytes));
        bytes[2] = 1;
        Assert.Throws<FormatException>(() => AuthenticatedControllerCommandCodec.ParseAndVerify(bytes, Secret));
        Assert.Throws<ArgumentException>(() => AuthenticatedControllerCommandCodec.Serialize(
            new AuthenticatedControllerCommandMessage(true, 33, 11, 11, 9, Nonce), Secret));
        Assert.Throws<FormatException>(() => AuthenticatedControllerCommandCodec.ParseSecret("not-a-key"));
    }

    [Fact]
    public void ReplayGuardRejectsDuplicatesAndBoundsItsWindow()
    {
        var guard = new ControllerCommandReplayGuard();
        var now = DateTimeOffset.UtcNow;
        Assert.True(guard.TryAccept(33, Nonce, now));
        Assert.False(guard.TryAccept(33, Nonce, now.AddSeconds(1)));
        Assert.True(guard.TryAccept(34, Nonce, now.AddSeconds(1)));
        Assert.True(guard.TryAccept(33, Nonce, now.AddMinutes(11)));
    }
}

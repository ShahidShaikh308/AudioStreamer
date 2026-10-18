using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.IPC.Tests;

public sealed class IpcRequestRateLimiterTests
{
    [Fact]
    public async Task ReadAndMutationBucketsAreIndependentAndRefillDeterministically()
    {
        var options = new IpcServerOptions
        {
            ReadRateLimitCapacity = 1,
            ReadRateLimitTokensPerSecond = 2,
            MutationRateLimitCapacity = 1,
            MutationRateLimitTokensPerSecond = 1
        };
        var clock = new ManualTimeProvider();
        var limiter = new IpcClientRequestRateLimiter(options, clock);
        var handler = new FakeHandler();
        var dispatcher = new RequestDispatcher(handler, handler, options, new IpcServerState(),
            NullLogger<RequestDispatcher>.Instance);
        var epoch = Guid.NewGuid();

        var firstRead = await DispatchAsync(dispatcher, limiter, epoch, "Ping", new PingPayload());
        var firstMutation = await DispatchAsync(dispatcher, limiter, epoch, "StartStream", new EmptyPayload());
        var limitedRead = await DispatchAsync(dispatcher, limiter, epoch, "Ping", new PingPayload());
        var limitedMutation = await DispatchAsync(dispatcher, limiter, epoch, "StartStream", new EmptyPayload());

        Assert.Equal("PingResult", firstRead.MessageType);
        Assert.Equal("StartStreamResult", firstMutation.MessageType);
        AssertRateLimited(limitedRead);
        AssertRateLimited(limitedMutation);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("PingResult", (await DispatchAsync(dispatcher, limiter, epoch, "Ping", new PingPayload())).MessageType);
        Assert.Equal("StartStreamResult", (await DispatchAsync(dispatcher, limiter, epoch, "StartStream", new EmptyPayload())).MessageType);
        Assert.Equal("PingResult", (await DispatchAsync(dispatcher,
            new IpcClientRequestRateLimiter(options, clock), epoch, "Ping", new PingPayload())).MessageType);
    }

    private static async Task<IpcEnvelope> DispatchAsync(RequestDispatcher dispatcher, IpcClientRequestRateLimiter limiter,
        Guid epoch, string type, object payload)
    {
        var request = IpcEnvelopeFactory.Request(type, Guid.NewGuid(), epoch, payload);
        return await dispatcher.DispatchAsync(request, epoch, new IpcClientIdentity("S-1-test", "test", true),
            new IpcClientSubscription(), limiter, CancellationToken.None);
    }

    private static void AssertRateLimited(IpcEnvelope response)
    {
        var error = IpcSerializer.DeserializePayload<ErrorResponse>(response.Payload);
        Assert.Equal("error", error.Result);
        Assert.Equal(IpcErrors.RateLimited, error.Error.Code);
        Assert.True(error.Error.Retryable);
    }

    private sealed class FakeHandler : IIpcRequestHandler, IIpcHandshakeInfoProvider
    {
        public ValueTask<IpcServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IpcServerInfo("service", "engine", "client", ["Ping"], "Running", "Idle"));

        public ValueTask<IpcHandlerResponse> HandleAsync(string messageType, System.Text.Json.JsonElement payload,
            IpcClientIdentity identity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IpcHandlerResponse(messageType + "Result",
                new IpcOperationResult<string> { Result = "ok", Data = messageType }));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}

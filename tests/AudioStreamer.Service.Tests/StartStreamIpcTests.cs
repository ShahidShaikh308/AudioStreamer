using System.Reflection;
using System.Text.Json;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using AudioStreamer.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class StartStreamIpcTests
{
    private static readonly Guid Sender = Guid.NewGuid();
    private static readonly Guid Receiver = Guid.NewGuid();

    [Fact]
    public async Task SuccessfulStartReturnsSerializableStartStreamResult()
    {
        var engine = CreateEngine(EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Streaming")));
        var response = await HandleAsync(engine, Sender, Receiver);

        Assert.Equal("StartStreamResult", response.MessageType);
        Assert.Equal("ok", response.Payload.GetType().GetProperty("Result")!.GetValue(response.Payload));
        Assert.NotNull(JsonSerializer.Serialize(response.Payload, IpcProtocol.JsonOptions));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidRouteReturnsStructuredFailureWithoutCallingEngine(bool emptySender, bool emptyReceiver)
    {
        var engine = CreateEngine(EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Streaming")));
        var response = await HandleAsync(engine, emptySender ? Guid.Empty : Sender, emptyReceiver ? Guid.Empty : Receiver);

        Assert.Equal("StartStreamResult", response.MessageType);
        var result = Assert.IsType<IpcOperationResult<object>>(response.Payload);
        Assert.Equal("error", result.Result);
        Assert.Equal(IpcErrors.InvalidField, result.Error!.Code);
        Assert.Equal(0, ((StreamEngineProxy)(object)engine).StartCalls);
    }

    [Theory]
    [InlineData(EngineErrorCode.NotFound, IpcErrors.PeerNotFound)]
    [InlineData(EngineErrorCode.CapabilityUnavailable, IpcErrors.CapabilityUnavailable)]
    [InlineData(EngineErrorCode.TransportUnavailable, IpcErrors.TransportUnavailable)]
    [InlineData(EngineErrorCode.InternalFailure, IpcErrors.StreamStartFailed)]
    public async Task EngineStartFailuresRemainStructuredAndSerializable(EngineErrorCode engineCode, string expectedCode)
    {
        var engine = CreateEngine(EngineResult<OperationReceipt>.Failure(engineCode, "audio", "The selected audio endpoint is unavailable.", true));
        var response = await HandleAsync(engine, Sender, Receiver);

        var result = Assert.IsType<IpcOperationResult<object>>(response.Payload);
        Assert.Equal("error", result.Result);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.NotNull(JsonSerializer.Serialize(result, IpcProtocol.JsonOptions));
    }

    [Fact]
    public async Task UnexpectedEngineExceptionBecomesStructuredFailure()
    {
        var engine = CreateEngine(exception: new InvalidOperationException("capture initialization failed"));
        var response = await HandleAsync(engine, Sender, Receiver);

        Assert.Equal("StartStreamResult", response.MessageType);
        var result = Assert.IsType<IpcOperationResult<object>>(response.Payload);
        Assert.Equal("error", result.Result);
        Assert.Equal(IpcErrors.StreamStartFailed, result.Error!.Code);
        Assert.DoesNotContain("capture initialization failed", result.Error.Message, StringComparison.Ordinal);
        Assert.NotNull(JsonSerializer.Serialize(result, IpcProtocol.JsonOptions));
    }

    private static Task<IpcHandlerResponse> HandleAsync(IEngine engine, Guid sender, Guid receiver) =>
        CreateHandler(engine).HandleAsync("StartStream",
            JsonSerializer.SerializeToElement(new { SenderNodeId = sender, ReceiverNodeId = receiver }, IpcProtocol.JsonOptions),
            new IpcClientIdentity("test", "test", false), CancellationToken.None).AsTask();

    private static EngineIpcRequestHandler CreateHandler(IEngine engine) => new(engine,
        new EventPublisher(new ConnectionManager(), NullLogger<EventPublisher>.Instance), new IpcServerState(),
        NullLogger<EngineIpcRequestHandler>.Instance);

    private static IEngine CreateEngine(EngineResult<OperationReceipt>? result = null, Exception? exception = null)
    {
        var engine = DispatchProxy.Create<IEngine, StreamEngineProxy>();
        var proxy = (StreamEngineProxy)(object)engine;
        proxy.StartResult = result;
        proxy.Exception = exception;
        return engine;
    }

    public class StreamEngineProxy : DispatchProxy
    {
        public EngineResult<OperationReceipt>? StartResult { get; set; }
        public Exception? Exception { get; set; }
        public int StartCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IEngine.StartStreamingAsync))
            {
                StartCalls++;
                if (Exception is not null) throw Exception;
                return Task.FromResult(StartResult ?? EngineResult<OperationReceipt>.Success(
                    new OperationReceipt(Guid.NewGuid(), "Streaming")));
            }
            if (targetMethod?.Name == nameof(IEngine.StopStreamingAsync))
                return Task.FromResult(EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Stopped")));
            throw new NotSupportedException($"Unexpected Engine call: {targetMethod?.Name}");
        }
    }

    private sealed class TestHandshakeInfo : IIpcHandshakeInfoProvider
    {
        public ValueTask<IpcServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IpcServerInfo("test", "test", "test", ["Ping", "StartStream"], "Running", "Running"));
    }
}

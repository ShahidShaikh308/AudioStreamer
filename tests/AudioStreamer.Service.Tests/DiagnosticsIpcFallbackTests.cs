using System.Reflection;
using System.Text.Json;
using AudioStreamer.Engine;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Transport;
using AudioStreamer.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using IpcDiagnosticsSnapshot = AudioStreamer.IPC.Contracts.DiagnosticsSnapshot;

namespace AudioStreamer.Service.Tests;

public sealed class DiagnosticsIpcFallbackTests
{
    [Fact]
    public async Task EngineExceptionReturnsSuccessfulUnavailableDiagnosticsResponse()
    {
        var engine = CreateThrowingEngine(new InvalidOperationException("runtime is shutting down"));
        var handler = CreateHandler(engine);

        var response = await handler.HandleAsync("GetDiagnostics", EmptyPayloadElement(),
            new IpcClientIdentity("S-1-test", "test", false), CancellationToken.None);

        Assert.Equal("GetDiagnosticsResult", response.MessageType);
        var result = Assert.IsType<IpcOperationResult<IpcDiagnosticsSnapshot>>(response.Payload);
        Assert.Equal("ok", result.Result);
        Assert.Equal("Unavailable", result.Data!.State);
        Assert.Null(result.Data.CpuPercent);
        Assert.Null(result.Data.WorkingSetBytes);
        Assert.Null(result.Data.Statistics);
        var serialized = JsonSerializer.Serialize(result.Data, AudioStreamer.IPC.Contracts.IpcProtocol.JsonOptions);
        Assert.Contains("Unavailable", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineFailureResultReturnsSuccessfulUnavailableDiagnosticsResponse()
    {
        var engine = CreateThrowingEngine(null, returnFailure: true);
        var handler = CreateHandler(engine);

        var response = await handler.HandleAsync("GetDiagnostics", EmptyPayloadElement(),
            new IpcClientIdentity("S-1-test", "test", false), CancellationToken.None);

        var result = Assert.IsType<IpcOperationResult<IpcDiagnosticsSnapshot>>(response.Payload);
        Assert.Equal("ok", result.Result);
        Assert.Equal("Unavailable", result.Data!.State);
    }

    private static EngineIpcRequestHandler CreateHandler(IEngine engine) => new(engine,
        new EventPublisher(new ConnectionManager(), NullLogger<EventPublisher>.Instance), new IpcServerState(),
        NullLogger<EngineIpcRequestHandler>.Instance);

    private static JsonElement EmptyPayloadElement() =>
        JsonSerializer.SerializeToElement(new EmptyPayload(), AudioStreamer.IPC.Contracts.IpcProtocol.JsonOptions);

    private static IEngine CreateThrowingEngine(Exception? exception, bool returnFailure = false)
    {
        var engine = DispatchProxy.Create<IEngine, DiagnosticsEngineProxy>();
        var proxy = (DiagnosticsEngineProxy)(object)engine;
        proxy.Exception = exception;
        proxy.ReturnFailure = returnFailure;
        return engine;
    }

    public class DiagnosticsEngineProxy : DispatchProxy
    {
        public Exception? Exception { get; set; }
        public bool ReturnFailure { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IEngine.GetDiagnosticsAsync))
                throw new NotSupportedException($"Unexpected Engine call: {targetMethod?.Name}");
            if (Exception is not null) throw Exception;
            if (ReturnFailure)
                return Task.FromResult(EngineResult<AudioStreamer.Engine.Contracts.DiagnosticsSnapshot>.Failure(
                    EngineErrorCode.InternalFailure, "diagnostics", "Metrics provider failed."));
            return Task.FromResult(EngineResult<AudioStreamer.Engine.Contracts.DiagnosticsSnapshot>.Success(
                new AudioStreamer.Engine.Contracts.DiagnosticsSnapshot(
                    new EngineStatus(EngineLifecycleState.Running, 1), [])));
        }
    }
}

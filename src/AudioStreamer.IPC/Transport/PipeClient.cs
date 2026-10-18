using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Protocol;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.IPC.Transport;

public sealed record IpcClientOptions
{
    public string PipeName { get; init; } = IpcProtocol.PipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int OutboundQueueCapacity { get; init; } = 64;
    public int EventQueueCapacity { get; init; } = 128;
    public TimeSpan HeartbeatInterval { get; init; } = IpcProtocol.HeartbeatInterval;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 200 || PipeName.Contains('\\') || PipeName.Contains('/'))
            throw new ArgumentException("IPC pipe name is invalid.", nameof(PipeName));
        if (ConnectTimeout <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (OutboundQueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(OutboundQueueCapacity));
        if (EventQueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(EventQueueCapacity));
    }
}

public sealed class IpcClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Async local client. Response correlation and pushed events are read from one pipe reader.</summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly ILogger<PipeClient> _logger;
    private readonly IpcClientOptions _options;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeClientStream? _pipe;
    private Channel<IpcEnvelope>? _outbound;
    private Channel<IpcEnvelope>? _events;
    private Task? _readerTask;
    private Task? _writerTask;
    private Task? _heartbeatTask;
    private IpcSchemaVersion? _schema;

    public PipeClient(ILogger<PipeClient> logger, IpcClientOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new IpcClientOptions();
        _options.Validate();
    }

    public Guid ConnectionEpoch { get; private set; }
    public HelloResult? Handshake { get; private set; }

    public async Task<HelloResult> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_pipe is not null) throw new InvalidOperationException("IPC client is already connected.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AudioStreamer IPC client currently targets Windows Named Pipes.");

        _pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        connectTimeout.CancelAfter(_options.ConnectTimeout);
        await _pipe.ConnectAsync(checked((int)_options.ConnectTimeout.TotalMilliseconds), connectTimeout.Token).ConfigureAwait(false);

        var requestId = Guid.NewGuid();
        var hello = new HelloRequest
        {
            SupportedVersions = ImmutableArray.Create(new IpcSchemaVersion(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion)),
            ClientVersion = typeof(PipeClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProcessId = Environment.ProcessId,
            RequestedRole = "interactive"
        };
        var helloEnvelope = IpcEnvelopeFactory.Request("Hello", requestId, Guid.Empty, hello);
        await IpcFrameCodec.WriteAsync(_pipe, helloEnvelope, connectTimeout.Token).ConfigureAwait(false);
        var response = await IpcFrameCodec.ReadAsync(_pipe, connectTimeout.Token, _options.ConnectTimeout).ConfigureAwait(false)
                       ?? throw new IpcClientException(IpcErrors.InvalidRequest, "Service closed the pipe during handshake.");

        if (response.RequestId != requestId || response.Kind != "response")
            throw new IpcClientException(IpcErrors.InvalidRequest, "Service returned an invalid handshake response.");
        if (response.MessageType != "HelloResult")
        {
            var rejection = IpcSerializer.DeserializePayload<ErrorResponse>(response.Payload);
            throw new IpcClientException(rejection.Error.Code, rejection.Error.Message);
        }

        var handshake = IpcSerializer.DeserializePayload<HelloResult>(response.Payload);
        Handshake = handshake;
        if (handshake.ConnectionEpoch == Guid.Empty || handshake.SelectedVersion.Major != IpcProtocol.MajorVersion)
            throw new IpcClientException(IpcErrors.IpcVersionUnsupported, "Service selected an unsupported IPC schema.");

        ConnectionEpoch = handshake.ConnectionEpoch;
        _schema = handshake.SelectedVersion;
        _outbound = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(_options.OutboundQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _events = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _writerTask = WriterLoopAsync(_lifetime.Token);
        _readerTask = ReaderLoopAsync(_lifetime.Token);
        _heartbeatTask = HeartbeatLoopAsync(_lifetime.Token);
        _logger.LogInformation("Connected to AudioStreamer Service. Epoch={ConnectionEpoch} Schema={SchemaMajor}.{SchemaMinor}",
            ConnectionEpoch, handshake.SelectedVersion.Major, handshake.SelectedVersion.Minor);
        return handshake;
    }

    public async Task<IpcEnvelope> RequestAsync<TPayload>(string messageType, TPayload payload, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("IPC client is not connected.");
        if (!pipe.IsConnected || _outbound is null || _schema is null)
            throw new IOException("IPC client is disconnected.");
        var requestId = Guid.NewGuid();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var request = IpcEnvelopeFactory.Request(messageType, requestId, ConnectionEpoch, payload, _schema);
            var requestPending = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, requestPending)) throw new InvalidOperationException("Request ID collision.");
            await _outbound.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => requestPending.TrySetCanceled(cancellationToken));
            var response = await requestPending.Task.ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "IPC request failed. RequestId={RequestId} MessageType={MessageType} ElapsedMs={ElapsedMs}",
                requestId, messageType, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public async IAsyncEnumerable<IpcEnvelope> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = _events ?? throw new InvalidOperationException("IPC client is not connected.");
        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _outbound?.Writer.TryComplete();
        _events?.Writer.TryComplete();
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        var tasks = new[] { _readerTask, _writerTask, _heartbeatTask }.Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        FailPending(new IOException("IPC client disconnected."));
        _pipe?.Dispose();
        _lifetime.Dispose();
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var writer = new ResponseWriter(_pipe!);
            await writer.RunAsync(_outbound!.Reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "IPC client writer failed.");
            _lifetime.Cancel();
            FailPending(new IOException("IPC client writer failed."));
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await IpcFrameCodec.ReadAsync(_pipe!, cancellationToken, IpcProtocol.IncompleteFrameTimeout).ConfigureAwait(false);
                if (envelope is null) break;
                if (envelope.ConnectionEpoch != ConnectionEpoch)
                    throw new IpcProtocolException(IpcErrors.InvalidRequest, "Service message belongs to a different connection epoch.");

                if (envelope.Kind == "response" && envelope.RequestId is { } requestId)
                {
                    if (_pending.TryGetValue(requestId, out var pending)) pending.TrySetResult(envelope);
                }
                else if (envelope.Kind == "event")
                {
                    if (_events is null || !_events.Writer.TryWrite(envelope))
                        throw new IpcProtocolException(IpcErrors.RateLimited, "Client event queue is full; resynchronize from a new snapshot.");
                }
                else
                {
                    throw new IpcProtocolException(IpcErrors.InvalidRequest, "Service sent an unsupported IPC message kind.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "IPC client reader disconnected.");
        }
        finally
        {
            _lifetime.Cancel();
            _outbound?.Writer.TryComplete();
            _events?.Writer.TryComplete();
            FailPending(new IOException("IPC connection closed."));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var response = await RequestAsync("Heartbeat", new EmptyPayload(), cancellationToken).ConfigureAwait(false);
                if (response.MessageType != "HeartbeatAck")
                    throw new IpcClientException(IpcErrors.InvalidRequest, "Service returned an invalid heartbeat response.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "IPC client heartbeat failed; connection will close.");
            _lifetime.Cancel();
            FailPending(new IOException("IPC heartbeat failed."));
            _pipe?.Dispose();
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var request in _pending.Values) request.TrySetException(exception);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Buffers;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.AudioAgent;

/// <summary>Owns all native audio adapters and their scoped lifetimes in the interactive Agent process.</summary>
public sealed class AgentAudioCommandHandler(IServiceScopeFactory scopes, IAgentAudioDeviceManager devices,
    AgentEventPublisher audioEvents, ILogger<AgentAudioCommandHandler> logger) : IAgentAudioCommandHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, CaptureEntry> _captures = new();
    private readonly ConcurrentDictionary<Guid, PlaybackEntry> _playbacks = new();
    private readonly ConcurrentDictionary<Guid, uint> _lastPlaybackSequences = new();
    private IAgentConnection? _captureConnection;
    private CancellationTokenSource? _captureConnectionLifetime;
    private CancellationTokenSource? _deviceWatcherLifetime;
    private Task? _deviceWatcher;
    private readonly SemaphoreSlim _audioEventReader = new(1, 1);
    private int _disposed;

    public async ValueTask<AgentFrame?> HandleControlAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        object response;
        AgentMessageType responseType;
        switch (request.MessageType)
        {
            case AgentMessageType.DeviceListRequest:
                response = new DeviceListResponse((await devices.GetEndpointsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(ToAgentDevice).ToArray());
                responseType = AgentMessageType.DeviceListResponse;
                break;
            case AgentMessageType.SelectionRequest:
                {
                    var selection = Read<AgentSelectionRequest>(request);
                    response = await devices.SelectAsync(selection, cancellationToken).ConfigureAwait(false);
                    responseType = AgentMessageType.SelectionResponse;
                    break;
                }
            case AgentMessageType.CaptureStart:
                response = await StartCaptureAsync(Read<AgentOperationRequest>(request), cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            case AgentMessageType.CaptureStop:
                response = await StopCaptureAsync(Read<AgentOperationRequest>(request).StreamId, cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            case AgentMessageType.CapturePause:
            case AgentMessageType.CaptureResume:
                response = await SetCapturePausedAsync(Read<AgentOperationRequest>(request).StreamId,
                    request.MessageType == AgentMessageType.CapturePause, cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            case AgentMessageType.PlaybackStart:
                response = await StartPlaybackAsync(Read<AgentOperationRequest>(request), cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            case AgentMessageType.PlaybackStop:
                response = await StopPlaybackAsync(Read<AgentOperationRequest>(request).StreamId, cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            case AgentMessageType.PlaybackPause:
            case AgentMessageType.PlaybackResume:
                response = await SetPlaybackPausedAsync(Read<AgentOperationRequest>(request).StreamId,
                    request.MessageType == AgentMessageType.PlaybackPause, cancellationToken).ConfigureAwait(false);
                responseType = AgentMessageType.OperationResponse;
                break;
            default: return null;
        }
        return new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control, responseType, 0,
            request.CorrelationId, JsonSerializer.SerializeToUtf8Bytes(response));
    }

    public void AttachControlConnection(IAgentConnection connection, CancellationToken cancellationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _deviceWatcherLifetime, lifetime);
        if (previous is not null)
        {
            _ = StopDeviceWatcherAsync(previous, _deviceWatcher);
        }
        _deviceWatcher = Task.WhenAll(PublishDeviceChangesAsync(connection, lifetime.Token),
            PublishAudioEventsAsync(connection, lifetime.Token));
    }

    private static async Task StopDeviceWatcherAsync(CancellationTokenSource lifetime, Task? worker)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }

    private async Task PublishDeviceChangesAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in devices.WatchChangesAsync(cancellationToken).ConfigureAwait(false))
            {
                var response = new DeviceListResponse(change.CurrentEndpoints.Select(ToAgentDevice).ToArray());
                await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control,
                    AgentMessageType.DeviceChanged, 0, Guid.Empty, JsonSerializer.SerializeToUtf8Bytes(response)), cancellationToken).ConfigureAwait(false);
                await RecoverChangedDevicesAsync(change, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException exception) { logger.LogInformation(exception, "Audio Agent control connection closed during device notification delivery."); }
        catch (Exception exception) { logger.LogError(exception, "Audio Agent device notification worker failed."); }
    }

    private async Task PublishAudioEventsAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        await _audioEventReader.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var audioEvent in audioEvents.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = new AgentPlatformEvent(audioEvent.Kind.ToString(), audioEvent.OccurredAt,
                    audioEvent.DeviceId, audioEvent.Count, audioEvent.SafeErrorCode);
                await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.Control,
                    AgentMessageType.PlatformEvent, 0, Guid.Empty, JsonSerializer.SerializeToUtf8Bytes(payload)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException exception) { logger.LogInformation(exception, "Audio Agent control connection closed during audio event delivery."); }
        catch (Exception exception) { logger.LogError(exception, "Audio Agent audio event worker failed."); }
        finally { _audioEventReader.Release(); }
    }

    private async Task RecoverChangedDevicesAsync(AudioDeviceChange change, CancellationToken cancellationToken)
    {
        foreach (var entry in _captures.Values.ToArray())
        {
            var captureDirection = entry.Request.CaptureSource == "Microphone" ? "capture" : "playback";
            if (!ShouldRecover(entry.Request.DeviceId, change, captureDirection)) continue;
            try
            {
                await StopCapturePumpAsync(entry).ConfigureAwait(false);
                await entry.Audio.StopAsync(cancellationToken).ConfigureAwait(false);
                await entry.Audio.StartAsync(ToCaptureOptions(entry.Request), cancellationToken).ConfigureAwait(false);
                if (_captureConnection is { IsConnected: true } connection && _captureConnectionLifetime is { } lifetime)
                    StartCapturePump(entry, connection, lifetime.Token);
                logger.LogInformation("Capture restarted after device change. StreamId={StreamId} DeviceId={DeviceId}", entry.StreamId, entry.Request.DeviceId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { logger.LogWarning(exception, "Capture recovery after device change failed. StreamId={StreamId}", entry.StreamId); }
        }
        foreach (var entry in _playbacks.ToArray())
        {
            if (!ShouldRecover(entry.Value.Request.DeviceId, change, "playback")) continue;
            try
            {
                await entry.Value.Audio.StopAsync(cancellationToken).ConfigureAwait(false);
                await entry.Value.Audio.StartAsync(ToPlaybackOptions(entry.Value.Request), cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Playback restarted after device change. StreamId={StreamId} DeviceId={DeviceId}", entry.Key, entry.Value.Request.DeviceId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { logger.LogWarning(exception, "Playback recovery after device change failed. StreamId={StreamId}", entry.Key); }
        }
    }

    private static bool ShouldRecover(string? selectedDevice, AudioDeviceChange change, string expectedDirection)
    {
        if (selectedDevice is not null)
            return string.Equals(selectedDevice, change.DeviceId, StringComparison.OrdinalIgnoreCase);

        if (change.Kind == AudioDeviceChangeKind.DefaultChanged) return true;
        if (change.Kind != AudioDeviceChangeKind.FormatChanged || change.DeviceId is null) return false;

        return change.CurrentEndpoints.Any(endpoint =>
            string.Equals(endpoint.Id, change.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(endpoint.Direction, expectedDirection, StringComparison.OrdinalIgnoreCase) &&
            endpoint.IsDefaultMultimedia);
    }

    public async Task RunCaptureChannelAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        _captureConnection = connection;
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureConnectionLifetime = connectionLifetime;
        try
        {
            foreach (var entry in _captures.Values) StartCapturePump(entry, connection, connectionLifetime.Token);
            var incoming = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException($"Unexpected request on capture PCM channel: {incoming.MessageType}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            connectionLifetime.Cancel();
            foreach (var entry in _captures.Values) await StopCapturePumpAsync(entry).ConfigureAwait(false);
            if (ReferenceEquals(_captureConnection, connection)) _captureConnection = null;
            if (ReferenceEquals(_captureConnectionLifetime, connectionLifetime)) _captureConnectionLifetime = null;
        }
    }

    public async Task RunPlaybackChannelAsync(IAgentConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Version != AgentProtocol.CurrentVersion || frame.MessageType != AgentMessageType.PlaybackPcm)
                throw new InvalidDataException("Invalid playback PCM frame.");
            var decoded = AgentProtocolCodec.DecodePcm(frame.Payload);
            if (_lastPlaybackSequences.TryGetValue(decoded.Metadata.StreamId, out var previousSequence) &&
                unchecked((int)(decoded.Metadata.SequenceNumber - previousSequence)) <= 0)
            {
                continue;
            }
            _lastPlaybackSequences[decoded.Metadata.StreamId] = decoded.Metadata.SequenceNumber;
            if (!_playbacks.TryGetValue(decoded.Metadata.StreamId, out var playback))
            {
                continue;
            }
            var format = ToAudioFormat(decoded.Metadata);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(decoded.Pcm.Length);
            try
            {
                decoded.Pcm.CopyTo(owner.Memory);
                using var audioFrame = new AudioFrame(owner, decoded.Pcm.Length, format,
                    decoded.Metadata.FrameCount, decoded.Metadata.TimestampTicks);
                owner = null!;
                _ = await playback.Audio.WriteAsync(audioFrame, cancellationToken).ConfigureAwait(false);
            }
            finally { owner?.Dispose(); }
        }
    }

    private async ValueTask<AgentOperationResponse> StartCaptureAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        if (request.StreamId == Guid.Empty || _captures.ContainsKey(request.StreamId)) return new(true);
        logger.LogInformation("Starting Agent capture stream. StreamId={StreamId} DeviceId={DeviceId}", request.StreamId, request.DeviceId);
        var scope = scopes.CreateAsyncScope();
        try
        {
            var capture = scope.ServiceProvider.GetRequiredService<IAudioCapture>();
            await capture.StartAsync(new AudioCaptureOptions(request.DeviceId,
                request.CaptureSource == "Microphone" ? AudioCaptureSource.Microphone : AudioCaptureSource.Loopback,
                ToAudioFormat(request), request.QueueCapacity), cancellationToken).ConfigureAwait(false);
            var entry = new CaptureEntry(scope, capture, request.StreamId, request);
            if (!_captures.TryAdd(request.StreamId, entry)) { await entry.DisposeAsync().ConfigureAwait(false); }
            else if (_captureConnection is { IsConnected: true } connection && _captureConnectionLifetime is { } lifetime)
                StartCapturePump(entry, connection, lifetime.Token);
            return new(true);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpCaptureAsync(CaptureEntry entry, IAgentConnection connection, CancellationToken cancellationToken)
    {
        await foreach (var frame in entry.Audio.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            using (frame)
            {
                var metadata = new AgentPcmMetadata(entry.StreamId, unchecked((uint)Interlocked.Increment(ref entry.Sequence)), frame.TimestampTicks,
                    frame.Format.SampleRate, frame.Format.Channels, frame.Format.SampleFormat.ToString(), frame.FrameCount);
                await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.CapturePcm,
                    AgentMessageType.CapturePcm, metadata.SequenceNumber, Guid.Empty, frame.Data, metadata), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<AgentOperationResponse> StopCaptureAsync(Guid streamId, CancellationToken cancellationToken)
    {
        if (!_captures.TryRemove(streamId, out var entry)) return new(true);
        await StopCapturePumpAsync(entry).ConfigureAwait(false);
        await entry.DisposeAsync().ConfigureAwait(false);
        return new(true);
    }

    private async ValueTask<AgentOperationResponse> SetCapturePausedAsync(Guid streamId, bool pause, CancellationToken cancellationToken)
    {
        if (!_captures.TryGetValue(streamId, out var entry)) return new(false, "stream-not-found", "Capture stream is not active.");
        if (pause) await entry.Audio.PauseAsync(cancellationToken).ConfigureAwait(false);
        else await entry.Audio.ResumeAsync(cancellationToken).ConfigureAwait(false);
        return new(true);
    }

    private async ValueTask<AgentOperationResponse> StartPlaybackAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        if (request.StreamId == Guid.Empty || _playbacks.ContainsKey(request.StreamId)) return new(true);
        logger.LogInformation("Starting Agent playback stream. StreamId={StreamId} DeviceId={DeviceId}", request.StreamId, request.DeviceId);
        var scope = scopes.CreateAsyncScope();
        try
        {
            var playback = scope.ServiceProvider.GetRequiredService<IAudioPlayback>();
            await playback.StartAsync(new AudioPlaybackOptions(request.DeviceId, ToAudioFormat(request),
                request.BufferDurationMilliseconds, request.RequestedLatencyMilliseconds), cancellationToken).ConfigureAwait(false);
            var entry = new PlaybackEntry(scope, playback, request);
            if (!_playbacks.TryAdd(request.StreamId, entry)) await entry.DisposeAsync().ConfigureAwait(false);
            return new(true);
        }
        catch { await scope.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask<AgentOperationResponse> StopPlaybackAsync(Guid streamId, CancellationToken cancellationToken)
    {
        if (!_playbacks.TryRemove(streamId, out var entry)) return new(true);
        _lastPlaybackSequences.TryRemove(streamId, out _);
        await entry.DisposeAsync().ConfigureAwait(false);
        return new(true);
    }

    private async ValueTask<AgentOperationResponse> SetPlaybackPausedAsync(Guid streamId, bool pause, CancellationToken cancellationToken)
    {
        if (!_playbacks.TryGetValue(streamId, out var entry)) return new(false, "stream-not-found", "Playback stream is not active.");
        if (pause) await entry.Audio.PauseAsync(cancellationToken).ConfigureAwait(false);
        else await entry.Audio.ResumeAsync(cancellationToken).ConfigureAwait(false);
        return new(true);
    }

    private static T Read<T>(AgentFrame frame) => JsonSerializer.Deserialize<T>(frame.Payload.Span)
        ?? throw new InvalidDataException($"Invalid {typeof(T).Name} payload.");

    private static AudioCaptureOptions ToCaptureOptions(AgentOperationRequest request) => new(request.DeviceId,
        request.CaptureSource == "Microphone" ? AudioCaptureSource.Microphone : AudioCaptureSource.Loopback,
        ToAudioFormat(request), request.QueueCapacity);
    private static AudioPlaybackOptions ToPlaybackOptions(AgentOperationRequest request) => new(request.DeviceId,
        ToAudioFormat(request), request.BufferDurationMilliseconds, request.RequestedLatencyMilliseconds);
    private static AudioFormat ToAudioFormat(AgentOperationRequest request) => new(request.SampleRate, request.Channels,
        Enum.TryParse<PcmSampleFormat>(request.SampleFormat, out var format) ? format : throw new InvalidDataException("Invalid sample format."));
    private static AudioFormat ToAudioFormat(AgentPcmMetadata metadata) => new(metadata.SampleRate, metadata.Channels,
        Enum.TryParse<PcmSampleFormat>(metadata.SampleFormat, out var format) ? format : throw new InvalidDataException("Invalid PCM sample format."));
    private static AgentDevice ToAgentDevice(AudioEndpointSnapshot endpoint) => new(endpoint.Id, endpoint.FriendlyName,
        endpoint.Direction, endpoint.IsDefaultMultimedia, endpoint.State == "Active", endpoint.State,
        endpoint.LoopbackCapable, endpoint.MixFormat?.SampleRate, endpoint.MixFormat?.Channels, endpoint.MixFormat?.SampleFormat.ToString());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in _captures.Keys) await StopCaptureAsync(id, CancellationToken.None).ConfigureAwait(false);
        foreach (var id in _playbacks.Keys) await StopPlaybackAsync(id, CancellationToken.None).ConfigureAwait(false);
        var watcherLifetime = Interlocked.Exchange(ref _deviceWatcherLifetime, null);
        if (watcherLifetime is not null)
        {
            await watcherLifetime.CancelAsync().ConfigureAwait(false);
            if (_deviceWatcher is not null)
            {
                try { await _deviceWatcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            watcherLifetime.Dispose();
        }
        _audioEventReader.Dispose();
    }

    private void StartCapturePump(CaptureEntry entry, IAgentConnection connection, CancellationToken cancellationToken)
    {
        lock (entry)
        {
            if (entry.Worker is { IsCompleted: false }) return;
            entry.PumpLifetime?.Dispose();
            entry.PumpLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            entry.Worker = SuperviseCapturePumpAsync(entry, connection, entry.PumpLifetime.Token);
        }
    }

    private async Task SuperviseCapturePumpAsync(CaptureEntry entry, IAgentConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await PumpCaptureAsync(entry, connection, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Audio Agent capture worker failed. StreamId={StreamId} Attempt={Attempt}", entry.StreamId, attempt);
                if (!connection.IsConnected) return;
                if (attempt == 3)
                {
                    try
                    {
                        await connection.SendAsync(new AgentFrame(AgentProtocol.CurrentVersion, AgentChannel.CapturePcm,
                            AgentMessageType.Error, 0, entry.StreamId,
                            JsonSerializer.SerializeToUtf8Bytes(new AgentError("capture-worker-failed",
                                "Audio capture failed after recovery attempts.", true))), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception sendException)
                    { logger.LogWarning(sendException, "Audio Agent could not report terminal capture failure. StreamId={StreamId}", entry.StreamId); }
                    return;
                }
                try
                {
                    await entry.Audio.StopAsync(cancellationToken).ConfigureAwait(false);
                    await entry.Audio.StartAsync(ToCaptureOptions(entry.Request), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception restartException)
                {
                    logger.LogError(restartException, "Audio Agent capture restart failed. StreamId={StreamId} Attempt={Attempt}", entry.StreamId, attempt);
                }
            }
        }
    }

    private static async Task StopCapturePumpAsync(CaptureEntry entry)
    {
        Task? worker;
        CancellationTokenSource? lifetime;
        lock (entry)
        {
            lifetime = entry.PumpLifetime;
            worker = entry.Worker;
            entry.PumpLifetime = null;
            entry.Worker = null;
        }
        if (lifetime is null) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        lifetime.Dispose();
    }

    private sealed class CaptureEntry(AsyncServiceScope scope, IAudioCapture audio, Guid streamId,
        AgentOperationRequest request) : IAsyncDisposable
    {
        public IAudioCapture Audio { get; } = audio;
        public Guid StreamId { get; } = streamId;
        public AgentOperationRequest Request { get; } = request;
        public long Sequence = request.InitialSequenceNumber;
        public Task? Worker { get; set; }
        public CancellationTokenSource? PumpLifetime { get; set; }
        public async ValueTask DisposeAsync()
        {
            await Audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (Worker is not null) { try { await Worker.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }
    private sealed class PlaybackEntry(AsyncServiceScope scope, IAudioPlayback audio, AgentOperationRequest request) : IAsyncDisposable
    {
        public IAudioPlayback Audio { get; } = audio;
        public AgentOperationRequest Request { get; } = request;
        public async ValueTask DisposeAsync() { await Audio.StopAsync(CancellationToken.None).ConfigureAwait(false); await scope.DisposeAsync().ConfigureAwait(false); }
    }
}

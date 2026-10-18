using AudioStreamer.Audio.Transport.Transport;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Managers;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Platform.Windows.Audio;

/// <summary>Composes sender or receiver lane resources for Engine-owned stream sessions.</summary>
internal sealed class WindowsAudioSessionRuntime(IServiceScopeFactory scopes, WindowsAudioStreamOptions options,
    IAudioPlatformEventPublisher events, ILogger<WindowsAudioSessionRuntime> logger) : IAudioSessionRuntime, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(Guid Sender, Guid Receiver), ActiveLane> _lanes = [];
    private AudioUdpReceiver? _sharedReceiver;
    private int _receiverRoutes;

    public async Task<EngineResult<bool>> StartAsync(StreamRequest request, Guid localNodeId,
        System.Net.IPEndPoint? audioEndpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = (request.SenderNodeId, request.ReceiverNodeId);
            if (_lanes.ContainsKey(key)) return EngineResult<bool>.Success(true);
            if (request.SenderNodeId != localNodeId && request.ReceiverNodeId != localNodeId)
                return EngineResult<bool>.Failure(EngineErrorCode.InvalidArgument, "session", "The route does not include this local node.");
            var scope = scopes.CreateAsyncScope();
            CancellationTokenSource? lifetime = null;
            Task? worker = null;
            Task? metrics = null;
            IAudioCapture? capture = null;
            IAudioPlayback? playback = null;
            AudioUdpSender? sender = null;
            AudioUdpReceiver? receiver = null;
            AudioReceiveQueue? routeQueue = null;
            ReceiverLaneTransport? receiverTransport = null;
            var receiverStartedHere = false;
            var receiverRouteCounted = false;
            try
            {
                var services = scope.ServiceProvider;
                lifetime = new CancellationTokenSource();
                if (request.SenderNodeId == localNodeId)
                {
                    if (audioEndpoint is null)
                        throw new InvalidOperationException("The receiver endpoint has not been discovered.");
                    capture = services.GetRequiredService<IAudioCapture>();
                    sender = new AudioUdpSender(audioEndpoint, options.Udp.SendQueueCapacity,
                        services.GetRequiredService<IAudioFormatConverter>(), logger);
                    logger.LogInformation("Starting Windows capture lane. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} DeviceId={DeviceId} Source={Source} Format={Format} Destination={Destination} SendQueueCapacity={SendQueueCapacity}",
                        key.SenderNodeId, key.ReceiverNodeId, options.CaptureDeviceId ?? "Default", AudioCaptureSource.Loopback,
                        options.Format, audioEndpoint, options.Udp.SendQueueCapacity);
                    await capture.StartAsync(new AudioCaptureOptions(options.CaptureDeviceId, AudioCaptureSource.Loopback,
                        options.Format), cancellationToken).ConfigureAwait(false);
                    sender.Start();
                    worker = PumpCaptureAsync(capture, sender, lifetime.Token);
                    metrics = PublishSenderMetricsAsync(sender, lifetime.Token);
                    _lanes.Add(key, new ActiveLane(scope, lifetime, capture, sender, worker, metrics));
                }
                else if (request.ReceiverNodeId == localNodeId)
                {
                    if (audioEndpoint is null)
                        throw new InvalidOperationException("The sender endpoint has not been discovered.");
                    playback = services.GetRequiredService<IAudioPlayback>();
                    receiver = _sharedReceiver ??= new AudioUdpReceiver(options.Udp.BindEndpoint,
                        options.Udp.ReceiveQueueCapacity, options.Udp.ReceiveBufferDuration);
                    routeQueue = receiver.RegisterSource(audioEndpoint.Address);
                    logger.LogInformation("Starting Windows playback lane. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} DeviceId={DeviceId} Format={Format} BindEndpoint={BindEndpoint} RemoteAddress={RemoteAddress} ReceiveQueueCapacity={ReceiveQueueCapacity} ReceiveBufferDuration={ReceiveBufferDuration}",
                        key.SenderNodeId, key.ReceiverNodeId, options.PlaybackDeviceId ?? "Default", options.Format,
                        options.Udp.BindEndpoint, audioEndpoint.Address, options.Udp.ReceiveQueueCapacity,
                        options.Udp.ReceiveBufferDuration);
                    // The 80 ms receive window can release short packet bursts. Give the
                    // Agent's render ring enough headroom to absorb them without changing
                    // the WASAPI device's requested latency.
                    await playback.StartAsync(new AudioPlaybackOptions(options.PlaybackDeviceId, options.Format,
                        BufferDurationMilliseconds: 250, RequestedLatencyMilliseconds: 50), cancellationToken).ConfigureAwait(false);
                    if (_receiverRoutes == 0) { await receiver.StartAsync(cancellationToken).ConfigureAwait(false); receiverStartedHere = true; }
                    _receiverRoutes++;
                    receiverRouteCounted = true;
                    receiverTransport = new ReceiverLaneTransport(receiver, audioEndpoint.Address, routeQueue);
                    worker = PumpPlaybackAsync(routeQueue, playback, services.GetRequiredService<IAudioBufferPool>(),
                        services.GetRequiredService<IAudioFormatConverter>(), key, lifetime.Token);
                    metrics = PublishReceiverMetricsAsync(receiver, routeQueue, key, lifetime.Token);
                    _lanes.Add(key, new ActiveLane(scope, lifetime, playback, receiverTransport, worker, metrics));
                }
                return EngineResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Windows audio lane initialization failed. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} CaptureDeviceId={CaptureDeviceId} PlaybackDeviceId={PlaybackDeviceId} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} ExceptionStackTrace={ExceptionStackTrace} InnerException={InnerException}",
                    request.SenderNodeId, request.ReceiverNodeId, options.CaptureDeviceId,
                    options.PlaybackDeviceId, exception.GetType().FullName, exception.Message,
                    exception.StackTrace, exception.InnerException?.ToString());
                lifetime?.Cancel();
                if (worker is not null)
                {
                    try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                if (metrics is not null)
                {
                    try { await metrics.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                if (receiverTransport is not null)
                {
                    await receiverTransport.DisposeAsync().ConfigureAwait(false);
                }
                else if (receiver is not null && routeQueue is not null)
                {
                    await receiver.UnregisterSourceAsync(audioEndpoint!.Address, routeQueue).ConfigureAwait(false);
                }
                if (receiverRouteCounted) _receiverRoutes--;
                if ((receiverStartedHere || _receiverRoutes == 0) && receiver is not null && _receiverRoutes == 0)
                    await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
                if (sender is not null)
                    await sender.DisposeAsync().ConfigureAwait(false);
                if (capture is not null)
                    await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                if (playback is not null)
                    await playback.StopAsync(CancellationToken.None).ConfigureAwait(false);
                lifetime?.Dispose();
                await scope.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Windows audio lane setup or rollback failed. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} ExceptionStackTrace={ExceptionStackTrace} InnerException={InnerException}",
                request.SenderNodeId, request.ReceiverNodeId, exception.GetType().FullName,
                exception.Message, exception.StackTrace, exception.InnerException?.ToString());
            return EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-transport", "The audio stream could not be started.", true);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult<bool>> StopAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = (request.SenderNodeId, request.ReceiverNodeId);
            if (!_lanes.Remove(key, out var lane)) return EngineResult<bool>.Success(true);
            var wasReceiver = lane.Transport is ReceiverLaneTransport;
            try { await lane.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (wasReceiver && --_receiverRoutes == 0 && _sharedReceiver is not null)
                    await _sharedReceiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return EngineResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Audio lane stop failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            return EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-transport", "The audio stream did not stop cleanly.", true);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult<bool>> UpdateEndpointAsync(StreamRequest request,
        System.Net.IPEndPoint audioEndpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioEndpoint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_lanes.TryGetValue((request.SenderNodeId, request.ReceiverNodeId), out var lane))
                return EngineResult<bool>.Failure(EngineErrorCode.NotFound, "session", "The sender session is not active.", true);
            switch (lane.Transport)
            {
                case AudioUdpSender sender:
                    sender.UpdateDestination(audioEndpoint);
                    break;
                case ReceiverLaneTransport receiver:
                    receiver.UpdateSource(audioEndpoint.Address);
                    break;
                default:
                    return EngineResult<bool>.Failure(EngineErrorCode.InvalidState, "session", "The active route has no audio endpoint to update.");
            }
            return EngineResult<bool>.Success(true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Audio lane endpoint update failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            return EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-transport", "The sender endpoint could not be updated.", true);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineResult<bool>> PauseAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
        await SetPausedAsync(request, isSender, pause: true, cancellationToken).ConfigureAwait(false);

    public async Task<EngineResult<bool>> ResumeAsync(StreamRequest request, bool isSender, CancellationToken cancellationToken) =>
        await SetPausedAsync(request, isSender, pause: false, cancellationToken).ConfigureAwait(false);

    private async Task<EngineResult<bool>> SetPausedAsync(StreamRequest request, bool isSender, bool pause,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_lanes.TryGetValue((request.SenderNodeId, request.ReceiverNodeId), out var lane))
                return EngineResult<bool>.Failure(EngineErrorCode.NotFound, "session", "The audio lane is not active.", true);
            if (isSender && lane.Endpoint is IAudioCapture capture)
            {
                if (pause) await capture.PauseAsync(cancellationToken).ConfigureAwait(false);
                else await capture.ResumeAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!isSender && lane.Endpoint is IAudioPlayback playback)
            {
                if (pause) await playback.PauseAsync(cancellationToken).ConfigureAwait(false);
                else await playback.ResumeAsync(cancellationToken).ConfigureAwait(false);
            }
            else return EngineResult<bool>.Failure(EngineErrorCode.InvalidState, "session", "The selected lane is not active on this node.");
            return EngineResult<bool>.Success(true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Audio lane pause/resume failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            return EngineResult<bool>.Failure(EngineErrorCode.TransportUnavailable, "audio-device", "The audio lane could not change pause state.", true);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var lane in _lanes.Values) await lane.DisposeAsync().ConfigureAwait(false);
            _lanes.Clear();
            if (_sharedReceiver is not null)
            {
                await _sharedReceiver.DisposeAsync().ConfigureAwait(false);
                _sharedReceiver = null;
                _receiverRoutes = 0;
            }
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task PumpCaptureAsync(IAudioCapture capture, AudioUdpSender sender, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in capture.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (!sender.Enqueue(frame))
                        await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.BufferOverrun,
                            DateTimeOffset.UtcNow, Count: 1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Audio capture worker failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.Error, DateTimeOffset.UtcNow,
                SafeErrorCode: "audio-send-worker-failed"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpPlaybackAsync(AudioReceiveQueue queue, IAudioPlayback playback, IAudioBufferPool buffers,
        IAudioFormatConverter converter, (Guid Sender, Guid Receiver) laneKey, CancellationToken cancellationToken)
    {
        var agentUnavailable = false;
        long rejectedFrames = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await queue.TakeAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null) break;
                var sourceFormat = new AudioFormat(packet.SampleRate, packet.Channels, PcmSampleFormat.Float32);
                var targetFormat = options.Format;
                var sourceFrames = packet.Payload.Length / sourceFormat.BytesPerFrame;
                var targetFrames = checked((int)Math.Round((double)sourceFrames * targetFormat.SampleRate / sourceFormat.SampleRate,
                    MidpointRounding.AwayFromZero));
                var byteLength = checked(targetFrames * targetFormat.BytesPerFrame);
                var owner = buffers.Rent(byteLength);
                try
                {
                    var frames = converter.Convert(packet.Payload.Span, sourceFormat, owner.Memory.Span[..byteLength], targetFormat);
                    using var frame = new AudioFrame(owner, checked(frames * targetFormat.BytesPerFrame), targetFormat, frames, 0);
                    owner = null!;
                    try
                    {
                        if (!await playback.WriteAsync(frame, cancellationToken).ConfigureAwait(false))
                        {
                            rejectedFrames += frames;
                            logger.LogWarning("Windows playback endpoint rejected PCM frames. SenderNodeId={SenderNodeId} ReceiverNodeId={ReceiverNodeId} Sequence={Sequence} FrameCount={FrameCount} RejectedFrames={RejectedFrames}",
                                laneKey.Sender, laneKey.Receiver, packet.SequenceNumber, frames, rejectedFrames);
                            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.BufferOverrun,
                                DateTimeOffset.UtcNow, Count: frames), cancellationToken).ConfigureAwait(false);
                        }
                        agentUnavailable = false;
                    }
                    catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Agent playback write failed; the route remains active for automatic reconnect. FrameCount={FrameCount}", frames);
                        if (!agentUnavailable)
                            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.Error, DateTimeOffset.UtcNow,
                                SafeErrorCode: "audio-agent-playback-disconnected"), cancellationToken).ConfigureAwait(false);
                        agentUnavailable = true;
                    }
                }
                finally { owner?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Audio playback worker failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} HResult={HResult} StackTrace={StackTrace} InnerException={InnerException} InnerStackTrace={InnerStackTrace}",
                exception.GetType().FullName, exception.Message, exception.HResult, exception.StackTrace,
                exception.InnerException?.ToString(), exception.InnerException?.StackTrace);
            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.Error, DateTimeOffset.UtcNow,
                SafeErrorCode: "audio-receive-worker-failed"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PublishSenderMetricsAsync(AudioUdpSender sender, CancellationToken cancellationToken)
    {
        long packets = 0, bytes = 0, dropped = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var current = sender.Snapshot;
            await PublishDeltaAsync(AudioPlatformEventKind.TransportPacketsSent, current.PacketsSent - packets, cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.TransportBytesSent, current.BytesSent - bytes, cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.TransportDroppedPackets, current.DroppedPackets - dropped, cancellationToken).ConfigureAwait(false);
            packets = current.PacketsSent; bytes = current.BytesSent; dropped = current.DroppedPackets;
        }
    }

    private async Task PublishReceiverMetricsAsync(AudioUdpReceiver receiver, AudioReceiveQueue queue,
        (Guid Sender, Guid Receiver) laneKey, CancellationToken cancellationToken)
    {
        long packets = 0, bytes = 0, dropped = 0, invalid = 0, reordered = 0, missing = 0, recovered = 0, underruns = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var current = receiver.Snapshot;
            var queueState = queue.Snapshot;
            var publishTransportMetrics = _lanes.Keys.OrderBy(key => key.Sender).ThenBy(key => key.Receiver).FirstOrDefault() == laneKey;
            if (publishTransportMetrics)
            {
                await PublishDeltaAsync(AudioPlatformEventKind.TransportPacketsReceived, current.PacketsReceived - packets, cancellationToken).ConfigureAwait(false);
                await PublishDeltaAsync(AudioPlatformEventKind.TransportBytesReceived, current.BytesReceived - bytes, cancellationToken).ConfigureAwait(false);
                await PublishDeltaAsync(AudioPlatformEventKind.TransportDroppedPackets, current.DroppedPackets - dropped, cancellationToken).ConfigureAwait(false);
                await PublishDeltaAsync(AudioPlatformEventKind.TransportInvalidPackets, current.InvalidPackets - invalid, cancellationToken).ConfigureAwait(false);
                packets = current.PacketsReceived; bytes = current.BytesReceived; dropped = current.DroppedPackets; invalid = current.InvalidPackets;
                await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.TransportQueueDepthPackets,
                    DateTimeOffset.UtcNow, Count: current.QueueDepth), cancellationToken).ConfigureAwait(false);
            }
            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.TransportQueueDurationMilliseconds,
                DateTimeOffset.UtcNow, Count: (long)queueState.BufferedDuration.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
            await events.PublishAsync(new AudioPlatformEvent(AudioPlatformEventKind.JitterBufferMilliseconds,
                DateTimeOffset.UtcNow, Count: (long)queueState.TargetLatency.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.PacketReordered, queueState.ReorderedPackets - reordered, cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.PacketMissing, queueState.MissingPackets - missing, cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.PacketRecovered, queueState.RecoveredPackets - recovered, cancellationToken).ConfigureAwait(false);
            await PublishDeltaAsync(AudioPlatformEventKind.JitterUnderrun, queueState.Underruns - underruns, cancellationToken).ConfigureAwait(false);
            reordered = queueState.ReorderedPackets; missing = queueState.MissingPackets;
            recovered = queueState.RecoveredPackets; underruns = queueState.Underruns;
        }
    }

    private ValueTask PublishDeltaAsync(AudioPlatformEventKind kind, long count, CancellationToken cancellationToken) =>
        count > 0 ? events.PublishAsync(new AudioPlatformEvent(kind, DateTimeOffset.UtcNow, Count: count), cancellationToken)
                  : ValueTask.CompletedTask;

    private sealed class ActiveLane(AsyncServiceScope scope, CancellationTokenSource lifetime, object endpoint,
        object transport, Task worker, Task metricsWorker) : IAsyncDisposable
    {
        public object Endpoint => endpoint;
        public object Transport => transport;
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            Exception? failure = null;
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception exception) { failure = exception; }
            try { await metricsWorker.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception exception) { failure ??= exception; }
            try
            {
                switch (endpoint)
                {
                    case IAudioCapture capture: await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); break;
                    case IAudioPlayback playback: await playback.StopAsync(CancellationToken.None).ConfigureAwait(false); break;
                }
            }
            catch (Exception exception) { failure ??= exception; }
            try
            {
                switch (transport)
                {
                    case AudioUdpSender sender: await sender.DisposeAsync().ConfigureAwait(false); break;
                    case ReceiverLaneTransport receiver: await receiver.DisposeAsync().ConfigureAwait(false); break;
                }
            }
            catch (Exception exception) { failure ??= exception; }
            lifetime.Dispose();
            try { await scope.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failure ??= exception; }
            if (failure is not null) throw new InvalidOperationException("An audio lane did not dispose cleanly.", failure);
        }
    }

    private sealed class ReceiverLaneTransport(AudioUdpReceiver receiver, System.Net.IPAddress sourceAddress,
        AudioReceiveQueue queue) : IAsyncDisposable
    {
        private System.Net.IPAddress _sourceAddress = sourceAddress;
        public void UpdateSource(System.Net.IPAddress currentAddress)
        {
            receiver.UpdateSource(_sourceAddress, currentAddress, queue);
            _sourceAddress = currentAddress;
        }
        public ValueTask DisposeAsync() => receiver.UnregisterSourceAsync(_sourceAddress, queue);
    }
}

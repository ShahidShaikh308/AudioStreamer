using System.Buffers;
using System.Collections.Immutable;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Platform.Windows.Audio;

/// <summary>Service-side capture port. Native audio stays in AudioAgent; PCM ownership is copied into a local pooled frame.</summary>
public sealed class AgentAudioCapture(IAgentAudioClient client, IAudioBufferPool buffers) : IAudioCapture
{
    private readonly Guid _streamId = Guid.NewGuid();
    private AudioCaptureOptions? _options;
    private string _state = "Stopped";
    public string State => Volatile.Read(ref _state);

    public async ValueTask StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken)
    {
        options.Format.EnsureSupported();
        var response = await client.StartCaptureAsync(ToRequest(options), cancellationToken).ConfigureAwait(false);
        Ensure(response);
        _options = options;
        Volatile.Write(ref _state, "Running");
    }
    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    { if (State != "Running") return; Ensure(await client.PauseCaptureAsync(_streamId, true, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Paused"); }
    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    { if (State != "Paused") return; Ensure(await client.PauseCaptureAsync(_streamId, false, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Running"); }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    { if (State == "Stopped") return; Ensure(await client.StopCaptureAsync(_streamId, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Stopped"); }
    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_options is null) throw new InvalidOperationException("Capture has not been started.");
        await foreach (var packet in client.ReadCaptureFramesAsync(_streamId, cancellationToken).ConfigureAwait(false))
        {
            var format = new AudioFormat(packet.Metadata.SampleRate, packet.Metadata.Channels,
                Enum.Parse<PcmSampleFormat>(packet.Metadata.SampleFormat, ignoreCase: false));
            var owner = buffers.Rent(packet.Pcm.Length);
            packet.Pcm.CopyTo(owner.Memory);
            yield return new AudioFrame(owner, packet.Pcm.Length, format, packet.Metadata.FrameCount, packet.Metadata.TimestampTicks);
        }
    }
    public async ValueTask DisposeAsync() { if (State != "Stopped") await StopAsync(CancellationToken.None).ConfigureAwait(false); }

    private AgentOperationRequest ToRequest(AudioCaptureOptions options) => new(_streamId, options.DeviceId,
        options.Format.SampleRate, options.Format.Channels, options.Format.SampleFormat.ToString(), options.Source.ToString(), options.QueueCapacity);
    private static void Ensure(AgentOperationResponse response)
    { if (!response.Succeeded) throw new IOException(response.SafeMessage ?? "Audio Agent capture operation failed."); }
}

/// <summary>Service-side playback port. PCM is serialized on the independent playback channel.</summary>
public sealed class AgentAudioPlayback(IAgentAudioClient client) : IAudioPlayback
{
    private readonly Guid _streamId = Guid.NewGuid();
    private AudioPlaybackOptions? _options;
    private string _state = "Stopped";
    private uint _sequence;
    public string State => Volatile.Read(ref _state);

    public async ValueTask StartAsync(AudioPlaybackOptions options, CancellationToken cancellationToken)
    {
        options.Format.EnsureSupported();
        var request = new AgentOperationRequest(_streamId, options.DeviceId, options.Format.SampleRate,
            options.Format.Channels, options.Format.SampleFormat.ToString(), BufferDurationMilliseconds: options.BufferDurationMilliseconds,
            RequestedLatencyMilliseconds: options.RequestedLatencyMilliseconds);
        Ensure(await client.StartPlaybackAsync(request, cancellationToken).ConfigureAwait(false));
        _options = options;
        Volatile.Write(ref _state, "Running");
    }
    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    { if (State != "Running") return; Ensure(await client.PausePlaybackAsync(_streamId, true, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Paused"); }
    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    { if (State != "Paused") return; Ensure(await client.PausePlaybackAsync(_streamId, false, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Running"); }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    { if (State == "Stopped") return; Ensure(await client.StopPlaybackAsync(_streamId, cancellationToken).ConfigureAwait(false)); Volatile.Write(ref _state, "Stopped"); }
    public async ValueTask<bool> WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        var options = _options ?? throw new InvalidOperationException("Playback has not been started.");
        if (frame.Format != options.Format) throw new ArgumentException("PCM format differs from active playback.", nameof(frame));
        var metadata = new AgentPcmMetadata(_streamId, unchecked(++_sequence), frame.TimestampTicks,
            frame.Format.SampleRate, frame.Format.Channels, frame.Format.SampleFormat.ToString(), frame.FrameCount);
        await client.WritePlaybackFrameAsync(metadata, frame.Data, cancellationToken).ConfigureAwait(false);
        return true;
    }
    public AudioLatencySnapshot GetLatency() => new(TimeSpan.Zero,
        TimeSpan.FromMilliseconds(_options?.RequestedLatencyMilliseconds ?? 0), System.Diagnostics.Stopwatch.GetTimestamp());
    public async ValueTask DisposeAsync() { if (State != "Stopped") await StopAsync(CancellationToken.None).ConfigureAwait(false); }
    private static void Ensure(AgentOperationResponse response)
    { if (!response.Succeeded) throw new IOException(response.SafeMessage ?? "Audio Agent playback operation failed."); }
}

/// <summary>Engine catalog adapter; endpoint activation and format probing run only in the Agent.</summary>
public sealed class AgentAudioDeviceCatalog(IAgentAudioClient client) : IAudioDeviceCatalog
{
    public async ValueTask<EngineResult<ImmutableArray<AudioDeviceInfo>>> EnumerateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.EnumerateDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (response.Devices.Count > 256) return EngineResult<ImmutableArray<AudioDeviceInfo>>.Failure(EngineErrorCode.InternalFailure, "audio-agent", "Audio Agent returned too many endpoints.");
            return EngineResult<ImmutableArray<AudioDeviceInfo>>.Success(response.Devices.Where(d => d.IsAvailable)
                .Select(d => new AudioDeviceInfo(d.Id, d.Name, d.Direction, d.IsDefault, d.IsAvailable)).ToImmutableArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return EngineResult<ImmutableArray<AudioDeviceInfo>>.Failure(EngineErrorCode.CapabilityUnavailable, "audio-agent", "Windows audio endpoints could not be enumerated.", true); }
    }
    public async ValueTask<EngineResult<bool>> SelectAsync(AudioDeviceSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SelectDevicesAsync(new AgentSelectionRequest(selection.CaptureDeviceId, selection.PlaybackDeviceId), cancellationToken).ConfigureAwait(false);
            return response.Succeeded ? EngineResult<bool>.Success(true) : EngineResult<bool>.Failure(EngineErrorCode.NotFound, "audio-agent", response.SafeMessage ?? "Selected endpoint is unavailable.", response.IsRetryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable, "audio-agent", "Audio Agent is unavailable.", true); }
    }
}

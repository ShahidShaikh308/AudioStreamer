using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer.AudioAgent.Audio.Devices;

/// <summary>Owns Core Audio endpoint notification registration and translates it to immutable endpoint snapshots.</summary>
public sealed class WindowsAudioDeviceManager : IAgentAudioDeviceManager
{
    private readonly IAudioPlatformEventPublisher _events;
    private readonly MMDeviceEnumerator _notificationEnumerator;
    private readonly MMDeviceNotificationClient _notificationClient;
    private readonly Channel<PendingChange> _pending = Channel.CreateBounded<PendingChange>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly object _watchersGate = new();
    private readonly List<Channel<AudioDeviceChange>> _watchers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _notificationWorker;
    private bool _disposed;

    public WindowsAudioDeviceManager(IAudioPlatformEventPublisher events)
    {
        _events = events;
        _notificationEnumerator = new MMDeviceEnumerator();
        _notificationClient = _notificationEnumerator.CreateNotificationClient();
        _notificationClient.DeviceAdded += (_, args) => Enqueue(AudioDeviceChangeKind.Added, args.DeviceId);
        _notificationClient.DeviceRemoved += (_, args) => Enqueue(AudioDeviceChangeKind.Removed, args.DeviceId);
        _notificationClient.DeviceStateChanged += (_, args) => Enqueue(AudioDeviceChangeKind.StateChanged, args.DeviceId);
        _notificationClient.DefaultDeviceChanged += (_, args) => Enqueue(AudioDeviceChangeKind.DefaultChanged, args.DeviceId);
        _notificationClient.PropertyValueChanged += (_, args) =>
        {
            // Core Audio raises this notification for many endpoint properties, including
            // high-frequency volume changes. Only a mix-format property change should cause
            // active audio clients to be reopened.
            var changedKey = args.PropertyKey;
            var deviceFormatKey = PropertyKeys.PKEY_AudioEngine_DeviceFormat;
            if (changedKey.formatId == deviceFormatKey.formatId &&
                changedKey.propertyId == deviceFormatKey.propertyId)
            {
                Enqueue(AudioDeviceChangeKind.FormatChanged, args.DeviceId);
            }
        };
        _notificationWorker = ProcessNotificationsAsync(_lifetime.Token);
    }

    public ValueTask<ImmutableArray<AudioEndpointSnapshot>> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateEndpointsAsync(cancellationToken);
    }

    internal static ValueTask<ImmutableArray<AudioEndpointSnapshot>> EnumerateEndpointsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = ImmutableArray.CreateBuilder<AudioEndpointSnapshot>();
        using var enumerator = new MMDeviceEnumerator();
        var defaults = GetDefaults(enumerator);
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            using (device)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var direction = device.DataFlow == DataFlow.Render ? "playback" : "capture";
                    var mix = TryGetMixFormat(device);
                    var formatList = GetSupportedFormats(device);
                    result.Add(new AudioEndpointSnapshot(device.ID, device.FriendlyName, direction, device.State.ToString(),
                        defaults.Console.Contains(device.ID), defaults.Multimedia.Contains(device.ID),
                        defaults.Communications.Contains(device.ID), device.DataFlow == DataFlow.Render, mix, formatList));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                { Trace.TraceWarning("Audio endpoint disappeared during enumeration: {0}", exception); }
            }
        }
        return ValueTask.FromResult(result.ToImmutable());
    }

    public async ValueTask<AgentOperationResponse> SelectAsync(AgentSelectionRequest selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (id, direction) in new[] { (selection.CaptureDeviceId, "capture"), (selection.PlaybackDeviceId, "playback") })
        {
            if (id is null) continue;
            if (!endpoints.Any(endpoint => endpoint.Id == id && endpoint.Direction == direction && endpoint.State == DeviceState.Active.ToString()))
                return new AgentOperationResponse(false, "endpoint-unavailable",
                    "Selected endpoint is missing, inactive, or has the wrong direction.");
        }
        return new AgentOperationResponse(true);
    }

    public async IAsyncEnumerable<AudioDeviceChange> WatchChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = Channel.CreateBounded<AudioDeviceChange>(new BoundedChannelOptions(32)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        lock (_watchersGate) _watchers.Add(channel);
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return change;
        }
        finally
        {
            lock (_watchersGate) _watchers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _pending.Writer.TryComplete();
        try { await _notificationWorker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        _notificationEnumerator.Dispose();
        lock (_watchersGate)
        {
            foreach (var watcher in _watchers) watcher.Writer.TryComplete();
            _watchers.Clear();
        }
        _lifetime.Dispose();
    }

    private void Enqueue(AudioDeviceChangeKind kind, string? id)
    {
        if (!_disposed) _pending.Writer.TryWrite(new PendingChange(kind, id));
    }

    private async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        await foreach (var pending in _pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
                var change = new AudioDeviceChange(pending.Kind, pending.DeviceId, DateTimeOffset.UtcNow, endpoints);
                Channel<AudioDeviceChange>[] watchers;
                lock (_watchersGate) watchers = _watchers.ToArray();
                foreach (var watcher in watchers) watcher.Writer.TryWrite(change);
                var eventKind = pending.Kind switch
                {
                    AudioDeviceChangeKind.Added => AudioPlatformEventKind.DeviceAdded,
                    AudioDeviceChangeKind.Removed => AudioPlatformEventKind.DeviceRemoved,
                    AudioDeviceChangeKind.DefaultChanged => AudioPlatformEventKind.DefaultChanged,
                    AudioDeviceChangeKind.FormatChanged => AudioPlatformEventKind.FormatChanged,
                    _ => AudioPlatformEventKind.DeviceLost
                };
                await _events.PublishAsync(new AudioPlatformEvent(eventKind, change.OccurredAt, pending.DeviceId), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            { Trace.TraceError("Windows audio device notification processing failed: {0}", exception); }
        }
    }

    private static (HashSet<string> Console, HashSet<string> Multimedia, HashSet<string> Communications) GetDefaults(MMDeviceEnumerator enumerator)
    {
        var console = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var multimedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var communications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDefault(enumerator, DataFlow.Render, Role.Console, console);
        AddDefault(enumerator, DataFlow.Capture, Role.Console, console);
        AddDefault(enumerator, DataFlow.Render, Role.Multimedia, multimedia);
        AddDefault(enumerator, DataFlow.Capture, Role.Multimedia, multimedia);
        AddDefault(enumerator, DataFlow.Render, Role.Communications, communications);
        AddDefault(enumerator, DataFlow.Capture, Role.Communications, communications);
        return (console, multimedia, communications);
    }

    private static void AddDefault(MMDeviceEnumerator enumerator, DataFlow flow, Role role, HashSet<string> output)
    {
        try { using var device = enumerator.GetDefaultAudioEndpoint(flow, role); output.Add(device.ID); }
        catch (Exception exception) { Trace.TraceWarning("Default audio endpoint lookup failed: {0}", exception); }
    }

    private static AudioFormat? TryGetMixFormat(MMDevice device)
    {
        try
        {
            using var client = device.CreateAudioClient();
            var format = client.MixFormat;
            var sample = format.BitsPerSample switch
            {
                16 => PcmSampleFormat.Int16,
                32 when format.Encoding == WaveFormatEncoding.IeeeFloat => PcmSampleFormat.Float32,
                _ => (PcmSampleFormat?)null
            };
            var candidate = sample is { } known ? new AudioFormat(format.SampleRate, format.Channels, known) : (AudioFormat?)null;
            return candidate is { IsSupported: true } supported ? supported : null;
        }
        catch (Exception exception) { Trace.TraceWarning("Audio endpoint mix format lookup failed: {0}", exception); return null; }
    }

    private static ImmutableArray<AudioFormat> GetSupportedFormats(MMDevice device)
    {
        var formats = ImmutableArray.CreateBuilder<AudioFormat>();
        try
        {
            using var client = device.CreateAudioClient();
            foreach (var rate in new[] { 44100, 48000 })
                foreach (var channels in new[] { 1, 2 })
                    foreach (var sample in new[] { PcmSampleFormat.Float32, PcmSampleFormat.Int16 })
                    {
                        var format = new AudioFormat(rate, channels, sample);
                        if (client.IsFormatSupported(AudioClientShareMode.Shared, WasapiAudioCapture.ToWaveFormat(format)))
                            formats.Add(format);
                    }
        }
        catch (Exception exception) { Trace.TraceWarning("Supported audio format enumeration failed: {0}", exception); }
        return formats.ToImmutable();
    }

    private sealed record PendingChange(AudioDeviceChangeKind Kind, string? DeviceId);
}

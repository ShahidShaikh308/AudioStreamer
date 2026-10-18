using System.Threading.Channels;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.Engine.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Service;

/// <summary>Refreshes Engine's existing device snapshot after Agent-published endpoint notifications.</summary>
public sealed class AudioAgentDeviceChangeService(IAgentAudioClient agent, IEngine engine,
    IAudioPlatformEventPublisher events,
    ILogger<AudioAgentDeviceChangeService> logger) : BackgroundService
{
    private long _pendingUnderruns;
    private long _pendingOverruns;
    private long _nextUnderrunReportTimestamp;
    private long _nextOverrunReportTimestamp;

    private readonly Channel<AgentNotification> _changes = Channel.CreateBounded<AgentNotification>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agent.DevicesChanged += OnDevicesChanged;
        agent.PlatformEventReceived += OnPlatformEvent;
        try
        {
            await foreach (var notification in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (notification.DevicesChanged)
                    {
                        var result = await engine.GetAudioDevicesAsync(stoppingToken).ConfigureAwait(false);
                        if (!result.IsSuccess) logger.LogWarning("Engine device refresh failed after Audio Agent notification. ErrorCode={ErrorCode}", result.Error?.Code);
                    }
                    if (notification.AudioEvent is { } audioEvent)
                        await events.PublishAsync(audioEvent, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Engine device refresh failed after Audio Agent notification."); }
            }
        }
        finally
        {
            agent.DevicesChanged -= OnDevicesChanged;
            agent.PlatformEventReceived -= OnPlatformEvent;
        }
    }

    private void OnDevicesChanged(DeviceListResponse _) => _changes.Writer.TryWrite(new AgentNotification(true, null));

    private void OnPlatformEvent(AgentPlatformEvent agentEvent)
    {
        if (!Enum.TryParse<AudioPlatformEventKind>(agentEvent.Kind, ignoreCase: false, out var kind))
        {
            logger.LogWarning("Audio Agent sent an unsupported platform event. Kind={Kind}", agentEvent.Kind);
            return;
        }
        if (kind is AudioPlatformEventKind.BufferUnderrun or AudioPlatformEventKind.BufferOverrun)
        {
            ref long pending = ref (kind == AudioPlatformEventKind.BufferUnderrun
                ? ref _pendingUnderruns
                : ref _pendingOverruns);
            ref long nextReport = ref (kind == AudioPlatformEventKind.BufferUnderrun
                ? ref _nextUnderrunReportTimestamp
                : ref _nextOverrunReportTimestamp);
            Interlocked.Add(ref pending, Math.Max(1, agentEvent.Count));
            if (!TryClaimDiagnosticReport(ref nextReport)) return;

            var count = Interlocked.Exchange(ref pending, 0);
            logger.LogWarning("Audio Agent audio diagnostic. Kind={Kind} DeviceId={DeviceId} Count={Count} SafeErrorCode={SafeErrorCode}",
                kind, agentEvent.DeviceId, count, agentEvent.SafeErrorCode);
            _changes.Writer.TryWrite(new AgentNotification(false,
                new AudioPlatformEvent(kind, DateTimeOffset.UtcNow, agentEvent.DeviceId, count, agentEvent.SafeErrorCode)));
            return;
        }

        if (kind is AudioPlatformEventKind.DeviceLost or AudioPlatformEventKind.Error)
            logger.LogWarning("Audio Agent audio diagnostic. Kind={Kind} DeviceId={DeviceId} Count={Count} SafeErrorCode={SafeErrorCode}",
                kind, agentEvent.DeviceId, agentEvent.Count, agentEvent.SafeErrorCode);
        else
            logger.LogInformation("Audio Agent audio state event. Kind={Kind} DeviceId={DeviceId} Count={Count}",
                kind, agentEvent.DeviceId, agentEvent.Count);
        _changes.Writer.TryWrite(new AgentNotification(false,
            new AudioPlatformEvent(kind, agentEvent.OccurredAt, agentEvent.DeviceId, agentEvent.Count, agentEvent.SafeErrorCode)));
    }

    private static bool TryClaimDiagnosticReport(ref long nextTimestamp)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var next = Volatile.Read(ref nextTimestamp);
            if (now < next) return false;
            if (Interlocked.CompareExchange(ref nextTimestamp,
                    now + System.Diagnostics.Stopwatch.Frequency, next) == next)
                return true;
        }
    }

    private sealed record AgentNotification(bool DevicesChanged, AudioPlatformEvent? AudioEvent);
}

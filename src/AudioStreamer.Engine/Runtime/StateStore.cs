using System.Collections.Immutable;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Runtime;

/// <summary>Single-writer immutable projection of all Engine-owned runtime facts.</summary>
internal sealed class StateStore : IAsyncDisposable
{
    private readonly EngineEventBus.Subscription _subscription;
    private readonly SemaphoreSlim _appliedSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _projectionTask;
    private EngineRuntimeSnapshot _current;
    private int _disposeStarted;

    public StateStore(EngineEventBus eventBus, Guid engineInstanceId)
    {
        _current = EngineRuntimeSnapshot.Create(engineInstanceId);
        _subscription = eventBus.Subscribe(critical: true);
        _projectionTask = ProjectAsync(_lifetime.Token);
    }

    public EngineRuntimeSnapshot Current => Volatile.Read(ref _current);

    public async Task WaitForSequenceAsync(long sequence, CancellationToken cancellationToken)
    {
        while (Current.EventWatermark < sequence)
            await _appliedSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _subscription.DisposeAsync().ConfigureAwait(false);
        try { await _projectionTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _appliedSignal.Dispose();
    }

    private async Task ProjectAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = Current;
            var next = message.Payload switch
            {
                NodeStateChangedPayload node => current with
                {
                    Status = node.Status,
                    NodeId = node.NodeId,
                    NodeName = node.NodeName,
                    EventWatermark = message.Sequence
                },
                SettingsChangedPayload settings => current with
                {
                    NodeId = settings.Settings.NodeId,
                    NodeName = settings.Settings.NodeName,
                    Settings = new SettingsSnapshot(settings.Settings.SchemaVersion, settings.Settings.Revision,
                        settings.Settings.NodeId, settings.Settings.NodeName),
                    EventWatermark = message.Sequence
                },
                PeersChangedPayload peers => current with { Peers = peers.Peers, EventWatermark = message.Sequence },
                SessionsChangedPayload sessions => current with { Sessions = sessions.Sessions, EventWatermark = message.Sequence },
                DevicesChangedPayload devices => current with { Devices = devices.Devices, EventWatermark = message.Sequence },
                StatisticsChangedPayload statistics => current with { Statistics = statistics.Statistics, EventWatermark = message.Sequence },
                DiagnosticsChangedPayload diagnostics => current with { Diagnostics = diagnostics.Diagnostics, EventWatermark = message.Sequence },
                _ => current with { EventWatermark = message.Sequence }
            };
            Volatile.Write(ref _current, next);
            if (_appliedSignal.CurrentCount == 0)
                _appliedSignal.Release();
        }
    }
}

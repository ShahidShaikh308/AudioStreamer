using System.Collections.Immutable;
using AudioStreamer.IPC.Contracts;

namespace AudioStreamer.UI.Core.Connection;

public sealed record ServiceSnapshot(
    NodeSnapshot? Node,
    SettingsSnapshot? Settings,
    ImmutableArray<IpcPeerSnapshot> Peers,
    ImmutableArray<IpcStreamSnapshot> Streams,
    ImmutableDictionary<string, long> Statistics,
    DiagnosticsSnapshot? Diagnostics,
    VersionSnapshot? Versions,
    ControlStatisticsSnapshot? ControlStatistics,
    DateTimeOffset SynchronizedAt)
{
    public static ServiceSnapshot Empty { get; } = new(null, null, [], [],
        ImmutableDictionary<string, long>.Empty, null, null, null, DateTimeOffset.MinValue);
}

public sealed record StatisticsPayload(DateTimeOffset SampledAt, ImmutableDictionary<string, long> Counters);

public enum ServiceConnectionState { Connecting, Synchronizing, Connected, Reconnecting, ServiceUnavailable }

public sealed record ServiceConnectionStatus(ServiceConnectionState State, string Message, string? ErrorCode = null,
    DateTimeOffset? ChangedAt = null);

public interface IUiDispatcher
{
    bool TryEnqueue(Action action);
}

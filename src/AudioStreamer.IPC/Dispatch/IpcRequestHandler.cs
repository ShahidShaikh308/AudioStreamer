using System.Text.Json;
using AudioStreamer.IPC.Transport;

namespace AudioStreamer.IPC.Dispatch;

public interface IIpcRequestHandler
{
    ValueTask<IpcHandlerResponse> HandleAsync(string messageType, JsonElement payload, IpcClientIdentity identity,
        CancellationToken cancellationToken);
}

public interface IIpcHandshakeInfoProvider
{
    ValueTask<IpcServerInfo> GetServerInfoAsync(CancellationToken cancellationToken);
}

public sealed record IpcServerInfo(string ServiceVersion, string EngineVersion, string MinimumClientVersion,
    IReadOnlyList<string> Capabilities, string ServiceState, string NodeState);

public sealed record IpcHandlerResponse(string MessageType, object Payload, string ResultCode = "ok");

public sealed class IpcClientSubscription
{
    private static readonly HashSet<string> AllowedEventKinds = new(StringComparer.Ordinal)
    {
        "SnapshotChanged", "PeerDiscovered", "PeerUpdated", "PeerLost", "NodeStateChanged", "LaneStateChanged",
        "StreamingStarted", "StreamingStopped", "StatisticsChanged", "DeviceListChanged", "DiagnosticsChanged",
        "OperationCompleted", "ServiceStopping", "ResyncRequired", "SettingsChanged", "Heartbeat"
    };
    private readonly object _sync = new();
    private HashSet<string>? _eventKinds;
    private bool _subscribed;
    private long _lastHeartbeatTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public bool IsSubscribed { get { lock (_sync) return _subscribed; } }

    public bool Subscribe(IReadOnlyList<string>? eventKinds)
    {
        if (eventKinds is { Count: > 32 }) return false;
        var kinds = eventKinds is null || eventKinds.Count == 0 ? null : new HashSet<string>(StringComparer.Ordinal);
        if (kinds is not null)
        {
            foreach (var kind in eventKinds!)
            {
                if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64 || !AllowedEventKinds.Contains(kind)) return false;
                kinds.Add(kind);
            }
        }
        lock (_sync) { _eventKinds = kinds; _subscribed = true; }
        return true;
    }

    public void Unsubscribe() { lock (_sync) { _eventKinds = null; _subscribed = false; } }

    public bool Accepts(string eventKind)
    {
        lock (_sync) return _subscribed && (_eventKinds is null || _eventKinds.Count == 0 || _eventKinds.Contains(eventKind));
    }

    public void MarkHeartbeat() => Interlocked.Exchange(ref _lastHeartbeatTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
    public TimeSpan TimeSinceHeartbeat => System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeatTimestamp));
}

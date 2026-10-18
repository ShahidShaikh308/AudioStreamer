using AudioStreamer.IPC.Contracts;
using AudioStreamer.UI.Core.Connection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.UI.Core.ViewModels;

public sealed partial class RuntimeStateViewModel : ObservableObject
{
    private readonly IpcConnectionCoordinator _connection;
    private readonly AppStateStore _appState;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RuntimeStateViewModel> _logger;
    private readonly List<MetricSample> _history = [];

    [ObservableProperty] private ServiceConnectionStatus _connectionStatus;
    [ObservableProperty] private string? _lastActionMessage;

    public RuntimeStateViewModel(IpcConnectionCoordinator connection, AppStateStore appState, IUiDispatcher dispatcher,
        ILogger<RuntimeStateViewModel> logger)
    {
        _connection = connection;
        _appState = appState;
        _dispatcher = dispatcher;
        _logger = logger;
        _connectionStatus = connection.Status;
        _appState.Changed += OnAppStateChanged;
        _appState.Notification += SetActionMessage;
        connection.ConnectionChanged += OnConnectionChanged;
    }

    public event Action<IReadOnlyList<MetricSample>>? StatisticsUpdated;
    public event Action? PeerChoicesChanged;
    public event Action<string?>? SnapshotChanged;
    public IReadOnlyList<MetricSample> MetricHistory => _history;
    public ServiceSnapshot Snapshot => _appState.Snapshot;
    public bool IsConnected => ConnectionStatus.State == ServiceConnectionState.Connected;
    public string NodeName => Snapshot.Settings?.NodeName ?? Snapshot.Node?.NodeName ?? "AudioStreamer node";
    public string NodeId => Snapshot.Node?.NodeId.ToString("D") ?? "Unavailable";
    public string NodeState => Snapshot.Node?.Status.State ?? "Unavailable";
    public int PeerCount => Snapshot.Peers.Length;
    public int StreamCount
    {
        get
        {
            var count = 0;
            foreach (var stream in Snapshot.Streams)
                if (AppStateStore.IsActiveSessionState(stream.State)) count++;
            return count;
        }
    }
    public string ServiceVersion => Snapshot.Versions?.ServiceVersion ?? _connection.Handshake?.ServiceVersion ?? "Unavailable";
    public string EngineVersion => Snapshot.Versions?.EngineVersion ?? _connection.Handshake?.EngineVersion ?? "Unavailable";
    public string IpcVersion => Snapshot.Versions?.IpcVersion ?? "1.0";
    public double? CpuPercent => Snapshot.Diagnostics?.CpuPercent;
    public string CpuText => CpuPercent is { } cpu ? $"{cpu:0.0}%" : "Unavailable";
    public string MemoryText => Snapshot.Diagnostics?.WorkingSetBytes is { } bytes ? FormatBytes(bytes) : "Unavailable";
    public string PacketLossText => Snapshot.Statistics.TryGetValue("packet_missing", out var lost) ? lost.ToString("N0") : "Unavailable";
    public string JitterText => Snapshot.Statistics.TryGetValue("jitter_buffer_milliseconds", out var jitter) ? $"{jitter} ms target" : "Unavailable";
    public string LatencyText => Snapshot.Statistics.TryGetValue("latency_samples", out var samples) && samples > 0 &&
        Snapshot.Statistics.TryGetValue("latency_average_microseconds", out var latency) ? $"{latency / 1000d:0.0} ms" : "Unavailable";
    public string QueueDepthText => Snapshot.Statistics.TryGetValue("queue_depth_packets", out var queueDepth)
        ? $"{queueDepth:N0} packets" : "Unavailable";
    public string BandwidthText => $"↑ {MetricRate("throughput_sent_bytes_per_second")}   ↓ {MetricRate("throughput_received_bytes_per_second")}";
    public string DiscoveryStatus => Snapshot.ControlStatistics is { } control
        ? $"{control.RegistrySize} peers · {control.PeerTimeouts} timeouts" : "Unavailable";
    public string HeartbeatStatus => Snapshot.ControlStatistics is { } control
        ? $"{control.Heartbeats:N0} received" : "Unavailable";
    public string UptimeText => Snapshot.Diagnostics is { } diagnostics ? diagnostics.Uptime.ToString(@"d\.hh\:mm\:ss") : "Unavailable";
    public bool HasData => Snapshot.Node is not null;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _connection.RefreshAsync(cancellationToken).ConfigureAwait(false);
            SetActionMessage("AudioStreamer state refreshed");
        }
        catch (Exception exception)
        {
            _logger.LogInformation("Manual AudioStreamer refresh failed. ErrorType={ErrorType}", exception.GetType().Name);
            SetActionMessage("Could not refresh service state");
        }
    }

    public Task<IpcEnvelope> RequestAsync<TPayload>(string type, TPayload payload, CancellationToken cancellationToken) =>
        _connection.RequestAsync(type, payload, cancellationToken);

    public void ApplySettings(SettingsSnapshot settings) => _appState.ApplySettings(settings);

    public void SetActionMessage(string message) => _dispatcher.TryEnqueue(() => LastActionMessage = message);

    private void NotifySnapshotChanged(string? eventType)
    {
        OnPropertyChanged(nameof(Snapshot));
        if (eventType is null)
        {
            OnPropertyChanged(nameof(NodeName));
            OnPropertyChanged(nameof(NodeId));
            OnPropertyChanged(nameof(NodeState));
            OnPropertyChanged(nameof(PeerCount));
            OnPropertyChanged(nameof(StreamCount));
            OnPropertyChanged(nameof(ServiceVersion));
            OnPropertyChanged(nameof(EngineVersion));
            OnPropertyChanged(nameof(IpcVersion));
            OnPropertyChanged(nameof(CpuPercent));
            OnPropertyChanged(nameof(CpuText));
            OnPropertyChanged(nameof(MemoryText));
            OnPropertyChanged(nameof(PacketLossText));
            OnPropertyChanged(nameof(JitterText));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(QueueDepthText));
            OnPropertyChanged(nameof(BandwidthText));
            OnPropertyChanged(nameof(DiscoveryStatus));
            OnPropertyChanged(nameof(HeartbeatStatus));
            OnPropertyChanged(nameof(UptimeText));
            OnPropertyChanged(nameof(HasData));
            PeerChoicesChanged?.Invoke();
            AddMetricSample();
        }
        else if (eventType is "PeerAdded" or "PeerUpdated" or "PeerRemoved")
        {
            OnPropertyChanged(nameof(PeerCount));
            PeerChoicesChanged?.Invoke();
        }
        else if (eventType is "SessionsChanged" or "SessionCreated" or "SessionPrepared" or "SessionStarted" or
            "SessionRecovered" or "SessionStopped" or "SessionFailed" or "SessionChanged")
        {
            OnPropertyChanged(nameof(StreamCount));
        }
        else if (eventType == "StatisticsChanged")
        {
            OnPropertyChanged(nameof(PacketLossText));
            OnPropertyChanged(nameof(JitterText));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(QueueDepthText));
            OnPropertyChanged(nameof(BandwidthText));
            AddMetricSample();
        }
        else if (eventType is "NodeStateChanged" or "DiagnosticsChanged")
        {
            OnPropertyChanged(nameof(NodeState));
            OnPropertyChanged(nameof(CpuPercent));
            OnPropertyChanged(nameof(CpuText));
            OnPropertyChanged(nameof(MemoryText));
            OnPropertyChanged(nameof(UptimeText));
        }
        else if (eventType == "SettingsChanged") OnPropertyChanged(nameof(NodeName));

        SnapshotChanged?.Invoke(eventType);
    }

    partial void OnConnectionStatusChanged(ServiceConnectionStatus value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ServiceVersion));
        OnPropertyChanged(nameof(EngineVersion));
    }

    private void OnConnectionChanged(ServiceConnectionStatus value) =>
        _dispatcher.TryEnqueue(() => ConnectionStatus = value);

    private void OnAppStateChanged(string? eventType) =>
        _dispatcher.TryEnqueue(() => NotifySnapshotChanged(eventType));

    private void AddMetricSample()
    {
        var now = DateTimeOffset.UtcNow;
        var statistics = Snapshot.Statistics;
        long? latency = statistics.TryGetValue("latency_samples", out var samples) && samples > 0 &&
            statistics.TryGetValue("latency_average_microseconds", out var averageLatency) ? averageLatency : null;
        var sample = new MetricSample(now, Metric(statistics, "throughput_sent_bytes_per_second"),
            Metric(statistics, "throughput_received_bytes_per_second"), Metric(statistics, "queue_depth_packets"),
            Metric(statistics, "jitter_buffer_milliseconds"), Metric(statistics, "packet_missing"),
            Metric(statistics, "packets_received"), Metric(statistics, "dropped_packets"),
            Metric(statistics, "packet_recovered"), latency);
        if (_history.Count > 0 && SameValues(_history[^1], sample)) return;
        _history.Add(sample);
        if (_history.Count > 60) _history.RemoveAt(0);
        StatisticsUpdated?.Invoke(_history.ToArray());
    }

    private static bool SameValues(MetricSample first, MetricSample second) =>
        first.SentBytesPerSecond == second.SentBytesPerSecond && first.ReceivedBytesPerSecond == second.ReceivedBytesPerSecond &&
        first.QueueDepthPackets == second.QueueDepthPackets && first.JitterTargetMilliseconds == second.JitterTargetMilliseconds &&
        first.PacketLossTotal == second.PacketLossTotal && first.PacketsReceived == second.PacketsReceived &&
        first.DroppedPackets == second.DroppedPackets && first.PacketsRecovered == second.PacketsRecovered &&
        first.LatencyAverageMicroseconds == second.LatencyAverageMicroseconds;

    private static long? Metric(IReadOnlyDictionary<string, long> statistics, string name) =>
        statistics.TryGetValue(name, out var value) ? value : null;
    private string MetricRate(string name) => Snapshot.Statistics.TryGetValue(name, out var value) ? FormatRate(value) : "Unavailable";
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0} KB";
    private static string FormatRate(long bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s" : $"{bytesPerSecond / 1024d:0} KB/s";
}

public sealed record MetricSample(DateTimeOffset At, long? SentBytesPerSecond, long? ReceivedBytesPerSecond,
    long? QueueDepthPackets, long? JitterTargetMilliseconds, long? PacketLossTotal,
    long? PacketsReceived, long? DroppedPackets, long? PacketsRecovered, long? LatencyAverageMicroseconds);

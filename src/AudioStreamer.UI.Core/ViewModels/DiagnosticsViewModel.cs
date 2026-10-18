using System.Text;
using AudioStreamer.UI.Core.Connection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioStreamer.UI.Core.ViewModels;

public sealed partial class DiagnosticsViewModel(RuntimeStateViewModel state) : ObservableObject
{
    [ObservableProperty] private string _notice = "Diagnostics are supplied by the AudioStreamer runtime.";
    public RuntimeStateViewModel State { get; } = state;
    public bool LogsAvailable => false;

    public string BuildExportText()
    {
        var snapshot = State.Snapshot;
        var text = new StringBuilder()
            .AppendLine("AudioStreamer support diagnostics")
            .AppendLine($"Exported UTC: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Connection: {State.ConnectionStatus.State}")
            .AppendLine($"AudioStreamer version: {snapshot.Versions?.ServiceVersion ?? "Unavailable"}")
            .AppendLine($"Engine version: {snapshot.Versions?.EngineVersion ?? "Unavailable"}")
            .AppendLine($"Node state: {snapshot.Node?.Status.State ?? "Unavailable"}")
            .AppendLine($"Peer count: {snapshot.Peers.Length}")
            .AppendLine($"Stream count: {snapshot.Streams.Length}")
            .AppendLine($"Safe error code: {snapshot.Diagnostics?.SafeErrorCode ?? "None"}")
            .AppendLine("Statistics:");
        foreach (var item in snapshot.Statistics.OrderBy(item => item.Key, StringComparer.Ordinal))
            text.Append("  ").Append(item.Key).Append(": ").AppendLine(item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }
}

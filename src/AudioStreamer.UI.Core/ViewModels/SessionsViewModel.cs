using System.Collections.ObjectModel;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.UI.Core.Connection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.UI.Core.ViewModels;

public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ILogger<SessionsViewModel> _logger;
    [ObservableProperty] private IpcPeerSnapshot? _selectedSender;
    [ObservableProperty] private IpcPeerSnapshot? _selectedReceiver;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _operationMessage;

    public RuntimeStateViewModel State { get; }
    public ObservableCollection<IpcPeerSnapshot> Senders { get; } = [];
    public ObservableCollection<IpcPeerSnapshot> Receivers { get; } = [];

    public SessionsViewModel(RuntimeStateViewModel state, ILogger<SessionsViewModel> logger)
    {
        State = state;
        _logger = logger;
        State.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RuntimeStateViewModel.ConnectionStatus)) StartCommand.NotifyCanExecuteChanged();
        };
        State.PeerChoicesChanged += RefreshPeerChoices;
        RefreshPeerChoices();
    }

    private void RefreshPeerChoices()
    {
        var senders = new List<IpcPeerSnapshot>();
        var receivers = new List<IpcPeerSnapshot>();
        var local = State.Snapshot.Node;
        if (local is not null)
        {
            // The local NodeSnapshot is authoritative, while transport capability bits are not part of IPC.
            // Include it as a route endpoint without fabricating advertised capabilities or network ports.
            var localEndpoint = new IpcPeerSnapshot(local.NodeId, "local", local.NodeName, "This device", 0,
                "Local node", 0, 0, local.Status.State, State.Snapshot.SynchronizedAt, State.Snapshot.Versions?.IpcVersion ?? "Unavailable");
            senders.Add(localEndpoint);
            receivers.Add(localEndpoint);
        }
        foreach (var peer in State.Snapshot.Peers)
        {
            if (!IsSelectable(peer.Status)) continue;
            if ((peer.Capabilities & 4) != 0) senders.Add(peer);
            if ((peer.Capabilities & 8) != 0) receivers.Add(peer);
        }
        var selectedSenderId = SelectedSender?.NodeId;
        var selectedReceiverId = SelectedReceiver?.NodeId;
        SynchronizeChoices(Senders, senders);
        SynchronizeChoices(Receivers, receivers);
        SelectedSender = selectedSenderId is { } senderId ? FindChoice(Senders, senderId) : null;
        SelectedReceiver = selectedReceiverId is { } receiverId ? FindChoice(Receivers, receiverId) : null;
    }

    private static IpcPeerSnapshot? FindChoice(ObservableCollection<IpcPeerSnapshot> choices, Guid id)
    {
        foreach (var peer in choices)
            if (peer.NodeId == id) return peer;
        return null;
    }

    private static bool IsSelectable(string status) => status.Equals("Online", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Streaming", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Busy", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Receiving", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Sending", StringComparison.OrdinalIgnoreCase);

    private static void SynchronizeChoices(ObservableCollection<IpcPeerSnapshot> current,
        IReadOnlyList<IpcPeerSnapshot> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var peer = desired[index];
            if (index < current.Count && current[index].NodeId == peer.NodeId)
            {
                if (current[index] != peer) current[index] = peer;
                continue;
            }

            var existingIndex = -1;
            for (var search = index + 1; search < current.Count; search++)
                if (current[search].NodeId == peer.NodeId) { existingIndex = search; break; }
            if (existingIndex >= 0)
            {
                current.Move(existingIndex, index);
                if (current[index] != peer) current[index] = peer;
            }
            else current.Insert(index, peer);
        }

        while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
    }

    public void SelectRouteForPeer(IpcPeerSnapshot peer)
    {
        var localId = State.Snapshot.Node?.NodeId;
        if (localId is null) return;
        var localSender = Senders.FirstOrDefault(item => item.NodeId == localId);
        var localReceiver = Receivers.FirstOrDefault(item => item.NodeId == localId);
        if ((peer.Capabilities & 8) != 0 && localSender is not null)
        {
            SelectedSender = localSender;
            SelectedReceiver = peer;
        }
        else if ((peer.Capabilities & 4) != 0 && localReceiver is not null)
        {
            SelectedSender = peer;
            SelectedReceiver = localReceiver;
        }
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedSender is null || SelectedReceiver is null || SelectedSender.NodeId == SelectedReceiver.NodeId) return;
        await RunRouteAsync("StartStream", SelectedSender.NodeId, SelectedReceiver.NodeId, cancellationToken);
    }

    [RelayCommand]
    private async Task StopAsync(IpcStreamSnapshot? session, CancellationToken cancellationToken)
    {
        if (session is null) return;
        await RunRouteAsync("StopStream", session.SenderNodeId, session.ReceiverNodeId, cancellationToken);
    }

    [RelayCommand]
    private Task PauseLocalAsync(IpcStreamSnapshot? session, CancellationToken cancellationToken) =>
        ChangeLocalLaneAsync(session, pause: true, cancellationToken: cancellationToken);

    [RelayCommand]
    private Task ResumeLocalAsync(IpcStreamSnapshot? session, CancellationToken cancellationToken) =>
        ChangeLocalLaneAsync(session, pause: false, cancellationToken: cancellationToken);

    private async Task ChangeLocalLaneAsync(IpcStreamSnapshot? session, bool pause, CancellationToken cancellationToken)
    {
        if (session is null) return;
        var localId = State.Snapshot.Node?.NodeId;
        if (localId is null) return;
        var sender = session.SenderNodeId == localId;
        var receiver = session.ReceiverNodeId == localId;
        if (!sender && !receiver) return;
        try
        {
            IsBusy = true;
            var type = pause ? "Pause" : "Resume";
            var response = await State.RequestAsync(type, new IpcLaneSelection(sender, receiver), cancellationToken);
            EnsureSuccess(response);
            OperationMessage = pause ? "Local audio lane paused" : "Local audio lane resumed";
        }
        catch (Exception exception)
        {
            _logger.LogInformation("Local lane action failed. ErrorType={ErrorType}", exception.GetType().Name);
            OperationMessage = "AudioStreamer could not change this device's audio lane";
        }
        finally { IsBusy = false; }
    }

    private bool CanStart() => !IsBusy && State.IsConnected && SelectedSender is not null && SelectedReceiver is not null &&
        SelectedSender.NodeId != SelectedReceiver.NodeId;

    partial void OnSelectedSenderChanged(IpcPeerSnapshot? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnSelectedReceiverChanged(IpcPeerSnapshot? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private async Task RunRouteAsync(string type, Guid sender, Guid receiver, CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            OperationMessage = type == "StartStream" ? "Requesting stream" : "Stopping stream";
            var response = await State.RequestAsync(type, new StreamRouteRequest(sender, receiver), cancellationToken);
            var result = AudioStreamer.IPC.Protocol.IpcSerializer.DeserializePayload<IpcOperationResult<IpcStreamOperation>>(response.Payload);
            if (result.Result != "ok")
            {
                var error = result.Error;
                OperationMessage = error is null
                    ? "AudioStreamer could not complete the stream request"
                    : $"{error.Code}: {error.Message}";
                return;
            }
            OperationMessage = type == "StartStream" ? "Stream request accepted" : "Stream stopped";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Stream command failed. Command={Command}", type);
            OperationMessage = "AudioStreamer could not complete the stream request";
        }
        finally { IsBusy = false; }
    }

    private static void EnsureSuccess(AudioStreamer.IPC.Contracts.IpcEnvelope response)
    {
        var result = AudioStreamer.IPC.Protocol.IpcSerializer.DeserializePayload<IpcOperationResult<IpcStreamOperation>>(response.Payload);
        if (result.Result != "ok") throw new InvalidOperationException(result.Error?.Message ?? "The operation failed.");
    }

    private sealed record StreamRouteRequest(Guid SenderNodeId, Guid ReceiverNodeId);
    private sealed record IpcStreamOperation(Guid OperationId, string State);
}

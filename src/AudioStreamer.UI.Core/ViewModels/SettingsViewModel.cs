using AudioStreamer.IPC.Contracts;
using AudioStreamer.UI.Core.Connection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.UI.Core.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _nodeName = string.Empty;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IUiPreferencesStore _preferences;
    private readonly SemaphoreSlim _preferenceSaveGate = new(1, 1);

    public SettingsViewModel(RuntimeStateViewModel state, ILogger<SettingsViewModel> logger, IUiPreferencesStore preferences)
    {
        State = state;
        _logger = logger;
        _preferences = preferences;
        var current = preferences.Load();
        _theme = current.Theme;
        _closeToTray = current.CloseToTray;
        _startMinimized = current.StartMinimized;
        State.SnapshotChanged += eventType =>
        {
            if (eventType is null or "SettingsChanged") NodeName = State.Snapshot.Settings?.NodeName ?? string.Empty;
        };
    }

    public RuntimeStateViewModel State { get; }
    public event Action<string>? ThemeChanged;

    public void LoadFromSnapshot()
    {
        NodeName = State.Snapshot.Settings?.NodeName ?? string.Empty;
    }

    partial void OnThemeChanged(string value) { _ = SavePreferencesAsync(); ThemeChanged?.Invoke(value); }
    partial void OnCloseToTrayChanged(bool value) => _ = SavePreferencesAsync();
    partial void OnStartMinimizedChanged(bool value) => _ = SavePreferencesAsync();

    private async Task SavePreferencesAsync()
    {
        await _preferenceSaveGate.WaitAsync();
        try { await _preferences.SaveAsync(new UiPreferences(Theme, CloseToTray, StartMinimized)); }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not persist UI preferences. ErrorType={ErrorType}", exception.GetType().Name);
            Message = "Your preference could not be saved.";
        }
        finally { _preferenceSaveGate.Release(); }
    }

    [RelayCommand]
    private async Task SaveNodeNameAsync(CancellationToken cancellationToken)
    {
        var settings = State.Snapshot.Settings;
        if (settings is null || string.IsNullOrWhiteSpace(NodeName) || NodeName.Length > 80)
        {
            Message = "Enter a node name up to 80 characters.";
            return;
        }
        try
        {
            IsBusy = true;
            var envelope = await State.RequestAsync("UpdateSettings",
                new UpdateSettingsRequest(settings.Revision, NodeName.Trim()), cancellationToken);
            var result = AudioStreamer.IPC.Protocol.IpcSerializer.DeserializePayload<IpcOperationResult<SettingsSnapshot>>(envelope.Payload);
            if (result.Result != "ok" || result.Data is null)
                throw new InvalidOperationException(result.Error?.Message ?? "AudioStreamer could not save the settings.");
            State.ApplySettings(result.Data);
            Message = "Node name saved";
        }
        catch (Exception exception)
        {
            _logger.LogInformation("Node settings update failed. ErrorType={ErrorType}", exception.GetType().Name);
            Message = "Settings could not be saved. Check your permissions and connection.";
        }
        finally { IsBusy = false; }
    }
}

namespace AudioStreamer.UI.Core.Connection;

public sealed record UiPreferences(string Theme = "System", bool CloseToTray = true,
    bool StartMinimized = false, bool NotificationsEnabled = false);

public interface IUiPreferencesStore
{
    UiPreferences Load();
    Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default);
}

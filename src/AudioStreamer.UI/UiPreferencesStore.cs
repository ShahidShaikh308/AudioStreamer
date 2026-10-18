using System.Text.Json;
using AudioStreamer.UI.Core.Connection;

namespace AudioStreamer.UI;

public sealed class UiPreferencesStore : IUiPreferencesStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioStreamer", "ui-preferences.json");

    public UiPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UiPreferences();
            return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(_path)) ?? new UiPreferences();
        }
        catch (Exception) { return new UiPreferences(); }
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

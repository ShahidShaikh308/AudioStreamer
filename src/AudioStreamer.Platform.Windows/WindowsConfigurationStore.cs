using System.Text.Json;
using AudioStreamer.Engine.Configuration;

namespace AudioStreamer.Platform.Windows;

/// <summary>Persists Engine-owned settings under the machine-wide product data directory.</summary>
public sealed class WindowsConfigurationStore : IConfigurationStore
{
    private const int MaximumConfigurationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly ILegacyNodeIdentityMapper? _identityMapper;

    public WindowsConfigurationStore(string? path = null, string? legacyPath = null,
        ILegacyNodeIdentityMapper? identityMapper = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AudioStreamer", "engine-settings.json");
        _legacyPath = legacyPath;
        _identityMapper = identityMapper;
    }

    public async ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            var legacyPath = LegacyWindowsConfiguration.FindPath(_legacyPath);
            if (legacyPath is null) return null;
            var legacy = LegacyWindowsConfiguration.Read(legacyPath);
            var nodeId = legacy.NodeId is { } legacyId
                ? (_identityMapper ?? throw new InvalidOperationException("Legacy node identity migration is not configured.")).ToEngineNodeId(legacyId)
                : Guid.NewGuid();
            var importedSettings = new EngineSettings(EngineSettings.CurrentSchemaVersion, 1, nodeId, legacy.DeviceName!);
            var dataDirectory = Path.GetDirectoryName(_path)!;
            var imported = new List<string> { "device_name" };
            if (legacy.NodeId.HasValue) imported.Add("node_id");
            if (legacy.ControlPort.HasValue) imported.Add("control_port");
            if (legacy.AudioReceivePort.HasValue) imported.Add("receive_port");
            LegacyWindowsConfiguration.PreserveBackup(legacyPath, dataDirectory, imported, legacy.UnsupportedKeys);
            await SaveAsync(importedSettings, cancellationToken).ConfigureAwait(false);
            return importedSettings;
        }
        var info = new FileInfo(_path);
        if (info.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("Engine configuration exceeds the size limit.");

        await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<EngineSettings>(input, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Engine configuration is empty.");
    }

    public async ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid) throw new ArgumentException("Engine settings are invalid.", nameof(settings));
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

using System.Globalization;
using System.Text.Json;

namespace AudioStreamer.Platform.Windows;

/// <summary>Bounded reader for the supported v1 Windows TOML key/value subset.</summary>
public sealed record LegacyWindowsConfiguration(string? DeviceName, ulong? NodeId, int? ControlPort,
    int? AudioReceivePort, IReadOnlyList<string> UnsupportedKeys)
{
    public static string? FindPath(string? explicitPath = null)
    {
        var configured = explicitPath ?? Environment.GetEnvironmentVariable("AUDIOSTREAMER_LEGACY_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "AudioStreamer", "config.local.toml"),
            Path.Combine(Environment.CurrentDirectory, "config", "config.local.toml")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static LegacyWindowsConfiguration Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Legacy configuration size is outside supported limits.");

        string? deviceName = null;
        ulong? nodeId = null;
        int? controlPort = null;
        int? receivePort = null;
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = RemoveComment(rawLine).Trim();
            if (line.Length == 0 || line.StartsWith("[", StringComparison.Ordinal)) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidDataException("Legacy configuration contains an invalid assignment.");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!seen.Add(key)) throw new InvalidDataException($"Legacy configuration repeats key '{key}'.");
            switch (key)
            {
                case "device_name": deviceName = ReadString(value, key); break;
                case "node_id": nodeId = ReadNodeId(value); break;
                case "control_port": controlPort = ReadPort(value, key, 1, 65535); break;
                case "receive_port": receivePort = ReadPort(value, key, 1, 65535); break;
                case "receiver_ip":
                case "receiver_port":
                case "audio_device_index":
                case "capture_queue_chunks":
                case "frames_per_buffer":
                case "log_level": unsupported.Add(key); break;
                default: unsupported.Add(key); break;
            }
        }

        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 80)
            throw new InvalidDataException("Legacy node name must contain 1 to 80 characters.");
        return new(deviceName.Trim(), nodeId, controlPort, receivePort, unsupported.ToArray());
    }

    public static void PreserveBackup(string sourcePath, string dataDirectory, IReadOnlyList<string> importedKeys,
        IReadOnlyList<string> unsupportedKeys)
    {
        var migrationDirectory = Path.Combine(dataDirectory, "MigrationBackups");
        Directory.CreateDirectory(migrationDirectory);
        var backupPath = Path.Combine(migrationDirectory, "config.local.toml.v1.bak");
        if (!File.Exists(backupPath)) File.Copy(sourcePath, backupPath);
        var report = new
        {
            SourceVersion = 1,
            MigratedAtUtc = DateTimeOffset.UtcNow,
            ImportedKeys = importedKeys,
            UnsupportedKeys = unsupportedKeys
        };
        WriteJsonAtomically(Path.Combine(migrationDirectory, "migration-report.json"), report);
    }

    internal static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Migration path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string RemoveComment(string line)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            else if (character == '#' && !quoted) return line[..index];
        }
        return line;
    }

    private static string ReadString(string value, string key)
    {
        try
        {
            if (value.StartsWith('"')) return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        }
        catch (JsonException) { }
        throw new InvalidDataException($"Legacy value '{key}' is not a supported string.");
    }

    private static ulong ReadNodeId(string value)
    {
        var text = value.Trim().Trim('"', '\'');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result) || result == 0)
            throw new InvalidDataException("Legacy node_id must be a nonzero hexadecimal 64-bit value.");
        return result;
    }

    private static int ReadPort(string value, string key, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
            throw new InvalidDataException($"Legacy value '{key}' is outside supported port bounds.");
        return result;
    }
}

/// <summary>Imports supported legacy transport ports into the ProgramData configuration overlay once.</summary>
public static class WindowsRuntimeConfigurationMigration
{
    public static string Prepare(string? legacyPath = null, string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AudioStreamer");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "runtime-settings.json");
        if (File.Exists(destination)) return destination;

        var source = LegacyWindowsConfiguration.FindPath(legacyPath);
        if (source is null) return destination;
        var legacy = LegacyWindowsConfiguration.Read(source);
        var imported = new List<string>();
        var root = new Dictionary<string, object>();
        if (legacy.ControlPort is { } controlPort)
        {
            root["ControlPlane"] = new Dictionary<string, object> { ["Port"] = controlPort };
            imported.Add("control_port");
        }
        if (legacy.AudioReceivePort is { } receivePort)
        {
            root["AudioTransport"] = new Dictionary<string, object> { ["ReceivePort"] = receivePort };
            imported.Add("receive_port");
        }
        if (root.Count > 0)
        {
            var migratedKeys = new List<string> { "device_name" };
            if (legacy.NodeId.HasValue) migratedKeys.Add("node_id");
            migratedKeys.AddRange(imported);
            LegacyWindowsConfiguration.PreserveBackup(source, directory, migratedKeys, legacy.UnsupportedKeys);
            LegacyWindowsConfiguration.WriteJsonAtomically(destination, root);
        }
        return destination;
    }
}

public interface ILegacyNodeIdentityMapper
{
    Guid ToEngineNodeId(ulong legacyProtocolNodeId);
}

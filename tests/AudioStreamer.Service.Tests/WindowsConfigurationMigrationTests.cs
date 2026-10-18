using System.Text.Json;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Platform.Windows;
using AudioStreamer.Service;
using AudioStreamer.Service.ControlPlane;
using AudioStreamer.Engine.Contracts;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class WindowsConfigurationMigrationTests
{
    [Fact]
    public async Task MigratesNodeIdentityAndSupportedPortsWithoutChangingControlProtocolId()
    {
        using var temporary = new TemporaryDirectory();
        var legacyPath = Path.Combine(temporary.Path, "config.local.toml");
        await File.WriteAllTextAsync(legacyPath, """
            device_name = "Legacy node"
            node_id = "37cfb45cb4de3dac"
            control_port = 55051
            receive_port = 5007
            receiver_ip = "192.0.2.10"
            receiver_port = 5005
            audio_device_index = -1
            """);

        var runtimePath = WindowsRuntimeConfigurationMigration.Prepare(legacyPath, temporary.Path);
        var enginePath = Path.Combine(temporary.Path, "engine-settings.json");
        var store = new WindowsConfigurationStore(enginePath, legacyPath, new LegacyNodeIdentityMapper());
        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(settings);
        Assert.Equal("Legacy node", settings.NodeName);
        Assert.Equal(0x37cfb45cb4de3dacUL, ControlNodeIdentity.ToProtocolId(settings.NodeId));
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(runtimePath));
        Assert.Equal(55051, runtime.RootElement.GetProperty("ControlPlane").GetProperty("Port").GetInt32());
        Assert.Equal(5007, runtime.RootElement.GetProperty("AudioTransport").GetProperty("ReceivePort").GetInt32());
        Assert.True(File.Exists(Path.Combine(temporary.Path, "MigrationBackups", "config.local.toml.v1.bak")));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temporary.Path, "MigrationBackups", "migration-report.json")));
        Assert.Contains("node_id", report.RootElement.GetProperty("ImportedKeys").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("audio_device_index", report.RootElement.GetProperty("UnsupportedKeys").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("37cfb45cb4de3dac", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "MigrationBackups", "migration-report.json")));
    }

    [Fact]
    public async Task RejectsInvalidLegacyConfigurationInsteadOfGeneratingAnotherIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var legacyPath = Path.Combine(temporary.Path, "config.local.toml");
        await File.WriteAllTextAsync(legacyPath, "device_name = \"Legacy node\"\nnode_id = \"not-hex\"\ncontrol_port = 80\n");
        var store = new WindowsConfigurationStore(Path.Combine(temporary.Path, "engine-settings.json"),
            legacyPath, new LegacyNodeIdentityMapper());

        await Assert.ThrowsAsync<InvalidDataException>(async () => { await store.LoadAsync(CancellationToken.None); });
        Assert.False(File.Exists(Path.Combine(temporary.Path, "engine-settings.json")));
    }

    [Fact]
    public async Task ExistingConfigurationWinsAndMigrationIsNotReapplied()
    {
        using var temporary = new TemporaryDirectory();
        var legacyPath = Path.Combine(temporary.Path, "config.local.toml");
        var settingsPath = Path.Combine(temporary.Path, "engine-settings.json");
        await File.WriteAllTextAsync(legacyPath, "device_name = \"Old node\"\nnode_id = \"0000000000000001\"\n");
        var store = new WindowsConfigurationStore(settingsPath, legacyPath, new LegacyNodeIdentityMapper());
        var original = await store.LoadAsync(CancellationToken.None);
        var changed = original! with { Revision = 2, NodeName = "Current node" };
        await store.SaveAsync(changed, CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(changed, reloaded);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioStreamer-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}

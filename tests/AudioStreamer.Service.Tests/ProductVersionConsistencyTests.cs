using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using AudioStreamer.Engine;
using AudioStreamerEngine = AudioStreamer.Engine.Engine;
using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.IPC.Contracts;
using AudioStreamer.IPC.Dispatch;
using AudioStreamer.IPC.Protocol;
using AudioStreamer.IPC.Transport;
using AudioStreamer.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class ProductVersionConsistencyTests
{
    [Fact]
    public async Task ProductVersionMatchesAssemblyMetadataAndIpcHandshake()
    {
        var engineAssembly = typeof(AudioStreamerEngine).Assembly;
        var serviceAssembly = typeof(EngineIpcRequestHandler).Assembly;
        var testAssembly = typeof(ProductVersionConsistencyTests).Assembly;
        var productVersion = InformationalVersion(engineAssembly);

        var repositoryRoot = FindRepositoryRoot();
        var sharedVersion = XDocument.Load(Path.Combine(repositoryRoot, "eng", "Version.props"))
            .Root!.Element("PropertyGroup")!.Element("Version")!.Value;
        Assert.Equal(sharedVersion, productVersion);
        var version = Version.Parse(productVersion);
        Assert.Equal(productVersion, InformationalVersion(serviceAssembly));
        Assert.Equal(productVersion, InformationalVersion(testAssembly));
        Assert.Equal(new Version(version.Major, version.Minor, version.Build, 0), engineAssembly.GetName().Version);
        Assert.Equal(engineAssembly.GetName().Version, serviceAssembly.GetName().Version);
        Assert.Equal(engineAssembly.GetName().Version, testAssembly.GetName().Version);
        Assert.Equal(engineAssembly.GetName().Version!.ToString(), FileVersion(engineAssembly));
        Assert.Equal(FileVersion(engineAssembly), FileVersion(serviceAssembly));
        Assert.Equal(FileVersion(engineAssembly), FileVersion(testAssembly));

        var installerProject = File.ReadAllText(Path.Combine(repositoryRoot, "installer", "AudioStreamer.Installer.wixproj"));
        var installerSource = File.ReadAllText(Path.Combine(repositoryRoot, "installer", "Package.wxs"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "windows-rc.yml"));
        Assert.Contains("ProductVersion=$(Version)", installerProject, StringComparison.Ordinal);
        Assert.Contains("Version=\"$(var.ProductVersion)\"", installerSource, StringComparison.Ordinal);
        Assert.Contains("eng/Version.props", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:Version=${{ env.PRODUCT_VERSION }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:ProductVersion=", workflow, StringComparison.Ordinal);

        await using var engine = new AudioStreamerEngine(new MemoryConfigurationStore(), NullLogger<AudioStreamerEngine>.Instance);
        var engineVersion = engine.GetVersionInfo();
        Assert.Equal(productVersion, engineVersion.ProductVersion);
        Assert.Equal(Version.Parse(productVersion).Major + "." + Version.Parse(productVersion).Minor,
            engineVersion.EngineApiVersion);

        var publisher = new EventPublisher(new ConnectionManager(), NullLogger<EventPublisher>.Instance);
        var requestHandler = new EngineIpcRequestHandler(engine, publisher, new IpcServerState(),
            NullLogger<EngineIpcRequestHandler>.Instance);
        var handshake = await new VersionHandshake(requestHandler).NegotiateAsync(
            IpcEnvelopeFactory.Request("Hello", Guid.NewGuid(), Guid.Empty, new HelloRequest
            {
                SupportedVersions = [new IpcSchemaVersion(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion)],
                ClientVersion = productVersion,
                ProcessId = Environment.ProcessId,
                RequestedRole = "interactive"
            }), new IpcClientIdentity("S-1-test", "test", false), CancellationToken.None);

        Assert.True(handshake.Accepted);
        var hello = IpcSerializer.DeserializePayload<HelloResult>(handshake.Response.Payload);
        Assert.Equal(productVersion, hello.ServiceVersion);
        Assert.Equal(productVersion, hello.EngineVersion);
        Assert.Equal(productVersion, hello.MinimumSupportedClientVersion);
    }

    private static string InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string? FileVersion(Assembly assembly) => FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AudioStreamer.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("AudioStreamer repository root was not found from the test output directory.");
    }

    private sealed class MemoryConfigurationStore : IConfigurationStore
    {
        public ValueTask<EngineSettings?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<EngineSettings?>(null);
        public ValueTask SaveAsync(EngineSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

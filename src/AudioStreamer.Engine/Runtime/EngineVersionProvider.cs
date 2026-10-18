using System.Reflection;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Engine.Runtime;

internal static class EngineVersionProvider
{
    public static EngineVersionInfo Current { get; } = Create();

    private static EngineVersionInfo Create()
    {
        var assembly = typeof(Engine).Assembly;
        var productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("Product informational version was not generated for the Engine assembly.");
        var semanticVersion = productVersion.Split('+', 2)[0];
        var version = Version.Parse(semanticVersion);
        var apiVersion = $"{version.Major}.{version.Minor}";
        return new EngineVersionInfo(apiVersion, productVersion, 3, 1);
    }
}

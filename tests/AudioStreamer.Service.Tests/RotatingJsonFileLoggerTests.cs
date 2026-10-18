using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class RotatingJsonFileLoggerTests
{
    [Fact]
    public void RedactsSensitiveStructuredFieldsAndFormattedMessage()
    {
        using var temporary = new TemporaryDirectory();
        using (var provider = new RotatingJsonFileLoggerProvider(temporary.Path))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("test").LogInformation("Connecting with {AccessToken}", "token-value-123");
        }

        var log = File.ReadAllText(Directory.GetFiles(temporary.Path, "*.jsonl").Single());
        using var document = JsonDocument.Parse(log);
        Assert.Equal("Connecting with [redacted]", document.RootElement.GetProperty("Message").GetString());
        Assert.Equal("[redacted]", document.RootElement.GetProperty("Properties").GetProperty("AccessToken").GetString());
        Assert.DoesNotContain("token-value-123", log, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioStreamer-logs", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}

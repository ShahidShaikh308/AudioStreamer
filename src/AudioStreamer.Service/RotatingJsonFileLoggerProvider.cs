using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Service;

/// <summary>Bounded asynchronous JSONL sink. A full queue drops the new log record rather than blocking a producer.</summary>
public sealed class RotatingJsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private const int RetainedFiles = 14;
    private const int MaximumMessageCharacters = 8192;
    private readonly string _directory;
    private readonly Channel<LogRecord> _queue = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly Task _writer;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private int _disposed;
    private long _dropped;

    public RotatingJsonFileLoggerProvider(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException exception) { Trace.TraceError("AudioStreamer log writer shutdown failed: {0}", exception.GetBaseException().GetType().Name); }
    }

    private void Enqueue(LogRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var path = SelectFile();
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await JsonSerializer.SerializeAsync(stream, record).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("AudioStreamer JSON log sink stopped: {0}", exception);
        }
    }

    private string SelectFile()
    {
        var stem = $"audiostreamer-{DateTime.UtcNow:yyyyMMdd}";
        for (var index = 0; ; index++)
        {
            var path = Path.Combine(_directory, $"{stem}-{index:D2}.jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes)
            {
                PruneFiles();
                return path;
            }
        }
    }

    private void PruneFiles()
    {
        var files = new DirectoryInfo(_directory).GetFiles("audiostreamer-*.jsonl")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        foreach (var old in files.Skip(RetainedFiles))
        {
            try { old.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger(RotatingJsonFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null) return;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs.Take(32))
                {
                    if (pair.Key == "{OriginalFormat}") continue;
                    properties[Limit(pair.Key, 96)] = IsSensitive(pair.Key) ? "[redacted]" : Limit(pair.Value?.ToString() ?? "null", 2048);
                }
            }

            // Scope payloads are intentionally omitted: they are arbitrary application values and
            // may contain identifiers or credentials that cannot be safely classified here.
            IReadOnlyList<string> scopes = Array.Empty<string>();
            var message = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> sensitivePairs)
            {
                foreach (var pair in sensitivePairs)
                {
                    if (!IsSensitive(pair.Key) || pair.Value is null) continue;
                    var sensitiveValue = pair.Value.ToString();
                    if (!string.IsNullOrEmpty(sensitiveValue))
                        message = message.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
                }
            }
            owner.Enqueue(new(DateTimeOffset.UtcNow, logLevel.ToString(), category, eventId.Id,
                Limit(message, MaximumMessageCharacters), exception?.GetType().FullName, properties, scopes));
        }

        private static bool IsSensitive(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("credential", StringComparison.OrdinalIgnoreCase) || key.Contains("pcm", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("payload", StringComparison.OrdinalIgnoreCase);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record LogRecord(DateTimeOffset TimestampUtc, string Level, string Category, int EventId,
        string Message, string? ExceptionType, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Scopes);
}

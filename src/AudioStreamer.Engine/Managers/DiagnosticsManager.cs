using System.Diagnostics;
using System.Collections.Immutable;
using System.ComponentModel;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;
using Microsoft.Extensions.Logging;

namespace AudioStreamer.Engine.Managers;

/// <summary>Samples process health and projects bounded Engine diagnostics at a configured minimum cadence.</summary>
internal sealed class DiagnosticsManager(EngineEventBus events, EngineOptions options, EngineVersionInfo version,
    IDiagnosticsMetricsProvider metrics, ILogger<DiagnosticsManager> logger)
{
    private readonly object _sync = new();
    private long _activeSinceTimestamp;
    private TimeSpan _accumulatedUptime;
    private long _lastSampleTimestamp;
    private TimeSpan _lastCpuTime;
    private double? _cpuPercent;
    private long? _workingSet;
    private EngineDiagnosticsSnapshot _current = EngineDiagnosticsSnapshot.Empty with { Version = version };

    public EngineDiagnosticsSnapshot Current => Volatile.Read(ref _current);

    public void MarkStarted()
    {
        lock (_sync)
            if (_activeSinceTimestamp == 0) _activeSinceTimestamp = Stopwatch.GetTimestamp();
    }

    public void MarkStopped()
    {
        lock (_sync)
        {
            if (_activeSinceTimestamp == 0) return;
            _accumulatedUptime += Stopwatch.GetElapsedTime(_activeSinceTimestamp);
            _activeSinceTimestamp = 0;
        }
    }

    public async ValueTask<EngineDiagnosticsSnapshot> SampleAsync(EngineStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = Stopwatch.GetTimestamp();
        EngineDiagnosticsSnapshot snapshot;
        lock (_sync)
        {
            if (_lastSampleTimestamp == 0 || Stopwatch.GetElapsedTime(_lastSampleTimestamp, now) >= options.EffectiveDiagnosticsSampleInterval)
            {
                var cpuTime = ReadMetric("CPU", metrics.GetCpuTime);
                if (_lastSampleTimestamp != 0 && cpuTime is { } currentCpuTime)
                {
                    var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now);
                    var cpuDelta = currentCpuTime - _lastCpuTime;
                    _cpuPercent = elapsed.TotalMilliseconds <= 0 ? null :
                        Math.Clamp(cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100d, 0d, 100d);
                }
                else if (cpuTime is null)
                {
                    _cpuPercent = null;
                }
                _lastCpuTime = cpuTime ?? _lastCpuTime;
                _lastSampleTimestamp = now;
                _workingSet = ReadMetric("memory", metrics.GetWorkingSetBytes);
            }

            var health = status.State switch
            {
                EngineLifecycleState.Running => "Healthy",
                EngineLifecycleState.Degraded => "Degraded",
                EngineLifecycleState.Faulted => "Faulted",
                _ => status.State.ToString()
            };
            var capabilities = ImmutableArray.Create("node-lifecycle", "configuration", "peer-registry",
                "session-state", "statistics", "diagnostics");
            var uptime = _accumulatedUptime;
            if (_activeSinceTimestamp != 0) uptime += Stopwatch.GetElapsedTime(_activeSinceTimestamp, now);
            snapshot = new EngineDiagnosticsSnapshot(health, uptime,
                _cpuPercent, _workingSet, version, capabilities, status.SafeErrorCode);
            Volatile.Write(ref _current, snapshot);
        }

        try
        {
            await events.PublishAsync("diagnostics", "DiagnosticsChanged", status,
                new DiagnosticsChangedPayload(snapshot), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException exception)
        {
            logger.LogDebug(exception, "Diagnostics event was not published because the Engine event bus is disposed.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Diagnostics snapshot is available but its optional event could not be published. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
        }
        return snapshot;
    }

    private T? ReadMetric<T>(string metric, Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or
            Win32Exception or PlatformNotSupportedException)
        {
            logger.LogDebug(exception,
                "Optional diagnostics metric is unavailable. Metric={Metric} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                metric, exception.GetType().FullName, exception.Message);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Optional diagnostics metric failed. Metric={Metric} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerException={InnerException}",
                metric, exception.GetType().FullName, exception.Message, exception.InnerException?.ToString());
            return null;
        }
    }
}

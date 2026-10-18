namespace AudioStreamer.Engine.Managers;

/// <summary>Optional process metrics source. A metric may be unavailable independently of the others.</summary>
internal interface IDiagnosticsMetricsProvider
{
    TimeSpan? GetCpuTime();
    long? GetWorkingSetBytes();
}

/// <summary>Reads metrics available from the current process without retaining a Process handle.</summary>
internal sealed class ProcessDiagnosticsMetricsProvider : IDiagnosticsMetricsProvider
{
    public TimeSpan? GetCpuTime()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    public long? GetWorkingSetBytes() => Environment.WorkingSet;
}

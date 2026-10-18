using AudioStreamer.IPC.Contracts;
using AudioStreamer.UI.Core.Connection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioStreamer.UI.Tests;

public sealed class DiagnosticsFallbackTests
{
    [Fact]
    public async Task DiagnosticsFailureReturnsUnavailableWithoutPropagating()
    {
        var result = await IpcConnectionCoordinator.GetDiagnosticsOrFallbackAsync(
            _ => Task.FromException<DiagnosticsSnapshot>(new InvalidOperationException("metrics unavailable")),
            CancellationToken.None, NullLogger.Instance);

        Assert.Equal("Unavailable", result.State);
        Assert.Null(result.CpuPercent);
        Assert.NotNull(result.Statistics);
    }

    [Fact]
    public async Task DiagnosticsTimeoutReturnsUnavailableWithoutPropagating()
    {
        var result = await IpcConnectionCoordinator.GetDiagnosticsOrFallbackAsync(
            _ => Task.FromException<DiagnosticsSnapshot>(new TimeoutException("deadline elapsed")),
            CancellationToken.None, NullLogger.Instance);

        Assert.Equal("Unavailable", result.State);
        Assert.True(result.WorkingSetBytes > 0);
    }

    [Fact]
    public async Task CallerCancellationStillPropagatesToStopSnapshotSynchronization()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IpcConnectionCoordinator.GetDiagnosticsOrFallbackAsync(
                _ => Task.FromCanceled<DiagnosticsSnapshot>(cancellation.Token), cancellation.Token, NullLogger.Instance));
    }

    [Fact]
    public async Task PartialDiagnosticsWithNullMetricsArePreserved()
    {
        var expected = new DiagnosticsSnapshot("Running", [], "Healthy", CpuPercent: null,
            WorkingSetBytes: null, Statistics: null);

        var result = await IpcConnectionCoordinator.GetDiagnosticsOrFallbackAsync(
            _ => Task.FromResult(expected), CancellationToken.None, NullLogger.Instance);

        Assert.Same(expected, result);
        Assert.Null(result.CpuPercent);
        Assert.Null(result.WorkingSetBytes);
        Assert.Null(result.Statistics);
    }

    [Fact]
    public async Task RepeatedDiagnosticsFailuresDoNotEscapeTheFallbackBoundary()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var result = await IpcConnectionCoordinator.GetDiagnosticsOrFallbackAsync(
                _ => Task.FromException<DiagnosticsSnapshot>(new ObjectDisposedException("metrics")),
                CancellationToken.None, NullLogger.Instance);
            Assert.Equal("Unavailable", result.State);
        }
    }

    [Fact]
    public async Task NetworkStatisticsFailureReturnsNullInsteadOfDisconnectingSnapshot()
    {
        var result = await IpcConnectionCoordinator.RequestOptionalMetricAsync<ControlStatisticsSnapshot>(
            () => Task.FromException<ControlStatisticsSnapshot>(new InvalidOperationException("control provider stopped")),
            null, "network statistics", CancellationToken.None, NullLogger.Instance);

        Assert.Null(result);
    }
}

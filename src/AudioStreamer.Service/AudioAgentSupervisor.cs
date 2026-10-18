using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Transport;
using Microsoft.Extensions.Hosting;

namespace AudioStreamer.Service;

public enum AgentState { Disconnected, Launching, Connecting, Connected, Reconnecting, Failed }

public sealed record AudioAgentStatus(AgentState AgentState, bool AgentConnected,
    DateTimeOffset? LastHeartbeat, int RestartCount, string? ErrorCode, bool Adopted = false, bool Launched = false);

public sealed record AgentSessionIdentity(int SessionId, string UserSid);

public interface IAudioAgentClient : IAgentClient
{
    DateTimeOffset? LastHeartbeat { get; }
    Task StartMonitoringAsync(CancellationToken cancellationToken);
    ValueTask ShutdownAgentAsync(CancellationToken cancellationToken);
    Task StopMonitoringAsync(CancellationToken cancellationToken);
}

public sealed class AudioAgentClient(AgentClient client) : IAudioAgentClient
{
    public bool IsConnected => client.IsConnected;
    public DateTimeOffset? LastHeartbeat => client.LastHeartbeat;
    public ValueTask ConnectAsync(CancellationToken cancellationToken) => client.ConnectAsync(cancellationToken);
    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => client.DisconnectAsync(cancellationToken);
    public ValueTask<AgentReady> HandshakeAsync(CancellationToken cancellationToken) => client.HandshakeAsync(cancellationToken);
    public ValueTask<DeviceListResponse> EnumerateDevicesAsync(CancellationToken cancellationToken) => client.EnumerateDevicesAsync(cancellationToken);
    public ValueTask<AgentOperationResponse> SelectDevicesAsync(AgentSelectionRequest request, CancellationToken cancellationToken) => client.SelectDevicesAsync(request, cancellationToken);
    public ValueTask<AgentOperationResponse> StartCaptureAsync(AgentOperationRequest request, CancellationToken cancellationToken) => client.StartCaptureAsync(request, cancellationToken);
    public ValueTask<AgentOperationResponse> StopCaptureAsync(Guid streamId, CancellationToken cancellationToken) => client.StopCaptureAsync(streamId, cancellationToken);
    public ValueTask<AgentOperationResponse> PauseCaptureAsync(Guid streamId, bool pause, CancellationToken cancellationToken) => client.PauseCaptureAsync(streamId, pause, cancellationToken);
    public ValueTask<AgentOperationResponse> StartPlaybackAsync(AgentOperationRequest request, CancellationToken cancellationToken) => client.StartPlaybackAsync(request, cancellationToken);
    public ValueTask<AgentOperationResponse> StopPlaybackAsync(Guid streamId, CancellationToken cancellationToken) => client.StopPlaybackAsync(streamId, cancellationToken);
    public ValueTask<AgentOperationResponse> PausePlaybackAsync(Guid streamId, bool pause, CancellationToken cancellationToken) => client.PausePlaybackAsync(streamId, pause, cancellationToken);
    public IAsyncEnumerable<(AgentPcmMetadata Metadata, ReadOnlyMemory<byte> Pcm)> ReadCaptureFramesAsync(Guid streamId, CancellationToken cancellationToken) => client.ReadCaptureFramesAsync(streamId, cancellationToken);
    public ValueTask WritePlaybackFrameAsync(AgentPcmMetadata metadata, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken) => client.WritePlaybackFrameAsync(metadata, pcm, cancellationToken);
    public ValueTask ShutdownAgentAsync(CancellationToken cancellationToken) => client.ShutdownAgentAsync(cancellationToken);
    public async Task StartMonitoringAsync(CancellationToken cancellationToken) => await client.StartAsync(cancellationToken).ConfigureAwait(false);
    public Task StopMonitoringAsync(CancellationToken cancellationToken) => client.StopAsync(cancellationToken);
}

public interface IAudioAgentSupervisor
{
    AudioAgentStatus Status { get; }
    Task RestartAsync(CancellationToken cancellationToken = default);
}

public interface IAgentProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void Kill();
}

public interface IAgentProcessLauncher
{
    AgentSessionIdentity GetActiveSessionIdentity();
    Task<IAgentProcess> LaunchAsync(string executablePath, CancellationToken cancellationToken);
    Task<IAgentProcess> AttachAsync(int processId, string executablePath, int expectedSessionId,
        CancellationToken cancellationToken);
}

public sealed record AudioAgentSupervisorOptions(TimeSpan StartupTimeout, TimeSpan ConnectAttemptTimeout,
    TimeSpan ShutdownTimeout, TimeSpan RestartBackoff, TimeSpan HealthPollInterval, int MaximumRestartAttempts,
    TimeSpan? RestartCooldown = null)
{
    public static AudioAgentSupervisorOptions Default { get; } = new(TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), 5);
    public TimeSpan EffectiveRestartCooldown => RestartCooldown ?? TimeSpan.FromSeconds(30);
}

/// <summary>Owns the interactive-session agent process and its lifecycle, independently of Engine audio behavior.</summary>
public sealed class AudioAgentSupervisor(IAudioAgentClient client, IAgentProcessLauncher launcher,
    AudioAgentSupervisorOptions options, ILogger<AudioAgentSupervisor> logger, TimeProvider timeProvider) : IAudioAgentSupervisor, IHostedService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IAgentProcess? _process;
    private Task? _monitorTask;
    private int _restartCount;
    private AgentState _state = AgentState.Disconnected;
    private string? _errorCode;
    private bool _stopping;
    private bool _monitoringStarted;
    private bool _adopted;
    private bool _launched;
    private AgentSessionIdentity? _connectedIdentity;
    private int? _connectedAgentProcessId;
    private bool _restartExhausted;
    private long _restartExhaustedAt;

    public AudioAgentStatus Status => new(_state, _state == AgentState.Connected && client.IsConnected,
        client.LastHeartbeat, _restartCount, _errorCode, _adopted, _launched);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        try
        {
            await ConnectOrLaunchAsync(isRestart: false, startup.Token).ConfigureAwait(false);
            _monitorTask = MonitorAsync(_lifetime.Token);
            logger.LogInformation("Audio Agent startup completed in {Elapsed}.", timeProvider.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAgentAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _state = AgentState.Failed;
            _errorCode = "AgentUnavailable";
            logger.LogError("Audio Agent startup timed out after {Elapsed}; service will continue with AgentUnavailable.", timeProvider.GetElapsedTime(startedAt));
            await StopAgentAsync(CancellationToken.None).ConfigureAwait(false);
            _state = AgentState.Failed;
            _monitorTask = MonitorAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            _state = AgentState.Failed;
            _errorCode = "AgentUnavailable";
            logger.LogError(exception, "Audio Agent startup failed after {Elapsed}; service will continue with AgentUnavailable.", timeProvider.GetElapsedTime(startedAt));
            await StopAgentAsync(CancellationToken.None).ConfigureAwait(false);
            _state = AgentState.Failed;
            _monitorTask = MonitorAsync(_lifetime.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _lifetime.Cancel();
        if (_monitorTask is not null)
            try { await _monitorTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        var cleanupTimeout = options.ShutdownTimeout > TimeSpan.Zero
            ? options.ShutdownTimeout + options.ShutdownTimeout
            : TimeSpan.FromSeconds(5);
        using var cleanup = new CancellationTokenSource(cleanupTimeout);
        await _lifecycle.WaitAsync(cleanup.Token).ConfigureAwait(false);
        try
        {
            await StopAgentAsync(cleanup.Token).ConfigureAwait(false);
            if (_monitoringStarted) await client.StopMonitoringAsync(cleanup.Token).ConfigureAwait(false);
            _monitoringStarted = false;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        _restartExhausted = false;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _state = AgentState.Reconnecting;
            logger.LogWarning("Restarting Audio Agent. RestartCount={RestartCount}", _restartCount + 1);
            await DisconnectCurrentAsync().ConfigureAwait(false);
            await RestartUntilConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RestartUntilConnectedAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= options.MaximumRestartAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            _restartCount++;
            var attemptStartedAt = timeProvider.GetTimestamp();
            logger.LogWarning("Audio Agent restart attempt {Attempt}/{Maximum}.", attempt, options.MaximumRestartAttempts);
            try
            {
                await Task.Delay(TimeSpan.FromTicks(options.RestartBackoff.Ticks * attempt), timeProvider, cancellationToken).ConfigureAwait(false);
                await ConnectOrLaunchAsync(isRestart: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                lastError = exception;
                logger.LogError(exception, "Audio Agent restart attempt {Attempt} failed after {Elapsed}. ExitCode={ExitCode}",
                    attempt, timeProvider.GetElapsedTime(attemptStartedAt), _process?.ExitCode);
                await DisconnectCurrentAsync().ConfigureAwait(false);
            }
        }
        _state = AgentState.Failed;
        _errorCode = "AgentUnavailable";
        _restartExhausted = true;
        _restartExhaustedAt = timeProvider.GetTimestamp();
        if (lastError is not null) logger.LogCritical(lastError, "Audio Agent exhausted {Maximum} restart attempts.", options.MaximumRestartAttempts);
    }

    private async Task ConnectOrLaunchAsync(bool isRestart, CancellationToken cancellationToken)
    {
        var expectedIdentity = launcher.GetActiveSessionIdentity();
        _state = isRestart ? AgentState.Reconnecting : AgentState.Launching;
        // Prefer the existing per-session Agent. A successful identity-checked handshake adopts it.
        var existing = await TryConnectAsync(expectedIdentity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (_process is { } previousProcess && previousProcess.Id != existing.ProcessId)
            {
                await previousProcess.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }
            var stillOwnedProcess = _process is { } owned && owned.Id == existing.ProcessId;
            await SetConnectionOriginAsync(existing, expectedIdentity, stillOwnedProcess, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DisconnectCurrentAsync().ConfigureAwait(false);
        if (_process is { } priorProcess)
        {
            if (!priorProcess.HasExited)
            {
                logger.LogWarning("Previously launched Audio Agent is alive but IPC is unavailable; terminating the verified process before relaunch. ProcessId={ProcessId}", priorProcess.Id);
                priorProcess.Kill();
                if (!await priorProcess.WaitForExitAsync(options.ShutdownTimeout, cancellationToken).ConfigureAwait(false))
                    throw new TimeoutException($"Unresponsive Audio Agent process {priorProcess.Id} did not exit after termination.");
                logger.LogInformation("Unresponsive Audio Agent exited. ProcessId={ProcessId} ExitCode={ExitCode}", priorProcess.Id, priorProcess.ExitCode);
            }
            await priorProcess.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }
        else if (_connectedAgentProcessId is { } previousAgentId && _connectedIdentity == expectedIdentity)
        {
            // A previously adopted Agent has already passed protocol and session identity
            // checks. If it stopped answering, attach only to that verified executable in
            // that session and replace it; never kill an arbitrary process by name.
            IAgentProcess? previousAgent = null;
            try
            {
                var previousAgentPath = Path.Combine(AppContext.BaseDirectory, "AudioStreamer.AudioAgent.exe");
                previousAgent = await launcher.AttachAsync(previousAgentId, previousAgentPath, expectedIdentity.SessionId,
                    cancellationToken).ConfigureAwait(false);
                if (!previousAgent.HasExited)
                {
                    logger.LogWarning("Previously adopted Audio Agent is alive but IPC is unavailable; terminating the verified process before relaunch. ProcessId={ProcessId}", previousAgent.Id);
                    previousAgent.Kill();
                    if (!await previousAgent.WaitForExitAsync(options.ShutdownTimeout, cancellationToken).ConfigureAwait(false))
                        throw new TimeoutException($"Unresponsive Audio Agent process {previousAgent.Id} did not exit after termination.");
                    logger.LogInformation("Unresponsive adopted Audio Agent exited. ProcessId={ProcessId} ExitCode={ExitCode}", previousAgent.Id, previousAgent.ExitCode);
                }
            }
            catch (ArgumentException exception)
            {
                // The process exited between disconnect detection and attachment.
                logger.LogInformation(exception, "Previously adopted Audio Agent process is no longer present. ProcessId={ProcessId}", previousAgentId);
            }
            finally
            {
                if (previousAgent is not null) await previousAgent.DisposeAsync().ConfigureAwait(false);
            }
            _connectedIdentity = null;
            _connectedAgentProcessId = null;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "AudioStreamer.AudioAgent.exe");
        logger.LogInformation("Launching Audio Agent. Executable={Executable}", path);
        _process = await launcher.LaunchAsync(path, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Audio Agent started. ProcessId={ProcessId}", _process.Id);
        _state = AgentState.Connecting;
        logger.LogInformation("Waiting for Audio Agent handshake.");
        var deadline = timeProvider.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(options.ConnectAttemptTimeout);
            try
            {
                var ready = await ConnectAndValidateAsync(expectedIdentity, attempt.Token).ConfigureAwait(false);
                var launchedProcess = _process;
                var launched = launchedProcess is not null && ready.ProcessId == launchedProcess.Id;
                if (launched && launchedProcess!.HasExited)
                    throw new InvalidOperationException($"Launched Audio Agent process exited during handshake. ExitCode={launchedProcess.ExitCode}.");
                if (!launched && launchedProcess is not null)
                {
                    // Another Service instance won the mutex race. Its Agent is the connected endpoint.
                    await launchedProcess.DisposeAsync().ConfigureAwait(false);
                    _process = null;
                }
                await SetConnectionOriginAsync(ready, expectedIdentity, launched, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Audio Agent connection attempt failed; retrying until startup deadline.");
                await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                if (exception is InvalidDataException || exception is AgentIdentityMismatchException { IsProtocolMismatch: true }) throw;
                if (exception is AgentIdentityMismatchException)
                {
                    try { await client.ShutdownAgentAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception shutdownException) { logger.LogWarning(shutdownException, "Stale Audio Agent shutdown request failed."); }
                    await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                if (_process is { HasExited: true } exitedProcess)
                    throw new InvalidOperationException($"Audio Agent exited before handshake. ExitCode={exitedProcess.ExitCode}.", exception);
            }
            if (timeProvider.GetElapsedTime(deadline) >= options.StartupTimeout) throw new TimeoutException("Audio Agent handshake deadline elapsed.");
            await Task.Delay(options.ConnectAttemptTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<AgentReady?> TryConnectAsync(AgentSessionIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(options.ConnectAttemptTimeout);
        try { return await ConnectAndValidateAsync(expectedIdentity, attempt.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); return null; }
        catch (AgentIdentityMismatchException exception) when (!exception.IsProtocolMismatch)
        {
            try { await client.ShutdownAgentAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception shutdownException) { logger.LogWarning(shutdownException, "Stale Audio Agent shutdown request failed."); }
            await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        { await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); return null; }
        catch
        { await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private async Task<AgentReady> ConnectAndValidateAsync(AgentSessionIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var ready = await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (ready.NegotiatedVersion != AgentProtocol.CurrentVersion)
            throw new AgentIdentityMismatchException($"Audio Agent protocol version mismatch. Expected={AgentProtocol.CurrentVersion}, Actual={ready.NegotiatedVersion}.", true);
        if (ready.SessionId != expectedIdentity.SessionId || !string.Equals(ready.UserSid, expectedIdentity.UserSid, StringComparison.OrdinalIgnoreCase))
            throw new AgentIdentityMismatchException($"Audio Agent belongs to a different interactive session or user. ExpectedSession={expectedIdentity.SessionId}, ActualSession={ready.SessionId}.");
        if (ready.ProcessId <= 0) throw new AgentIdentityMismatchException("Audio Agent handshake did not provide a valid process identity.", true);
        return ready;
    }

    private async Task SetConnectionOriginAsync(AgentReady ready, AgentSessionIdentity expectedIdentity,
        bool launched, CancellationToken cancellationToken)
    {
        _state = AgentState.Connected;
        _errorCode = null;
        _launched = launched;
        _adopted = !launched;
        _connectedIdentity = expectedIdentity;
        _connectedAgentProcessId = ready.ProcessId;
        logger.LogInformation("Audio Agent handshake successful. InstanceId={InstanceId} ProtocolVersion={ProtocolVersion} SessionId={SessionId} UserSid={UserSid} ProcessId={ProcessId} Origin={Origin}",
            ready.InstanceId, ready.NegotiatedVersion, expectedIdentity.SessionId, expectedIdentity.UserSid, ready.ProcessId, launched ? "Launched" : "Adopted");
        if (!_monitoringStarted)
        {
            await client.StartMonitoringAsync(_lifetime.Token).ConfigureAwait(false);
            _monitoringStarted = true;
        }
    }

    private async Task DisconnectCurrentAsync()
    {
        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _state = AgentState.Disconnected;
        _adopted = false;
        _launched = false;
        // Keep the last handshake-verified identity and PID until the next connection
        // succeeds. They let recovery safely attach to a previously adopted Agent.
    }

    private sealed class AgentIdentityMismatchException(string message, bool isProtocolMismatch = false) : Exception(message)
    {
        public bool IsProtocolMismatch { get; } = isProtocolMismatch;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.HealthPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                if (_restartExhausted && timeProvider.GetElapsedTime(_restartExhaustedAt) >= options.EffectiveRestartCooldown)
                {
                    _restartExhausted = false;
                    logger.LogWarning("Audio Agent restart cooldown elapsed; resuming bounded restart attempts.");
                }
                var process = _process;
                if (_state == AgentState.Connected && _connectedIdentity is { } connectedIdentity)
                {
                    AgentSessionIdentity activeIdentity;
                    try { activeIdentity = launcher.GetActiveSessionIdentity(); }
                    catch (InvalidOperationException)
                    {
                        logger.LogWarning("No active interactive session remains; stopping the adopted Audio Agent.");
                        await RestartForSessionChangeAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (activeIdentity != connectedIdentity)
                    {
                        logger.LogInformation("Active user session changed. PreviousSession={PreviousSession} CurrentSession={CurrentSession}; reconnecting Audio Agent.",
                            connectedIdentity.SessionId, activeIdentity.SessionId);
                        await RestartForSessionChangeAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                if (process is not null && process.HasExited)
                {
                    logger.LogError("Audio Agent process exited. ProcessId={ProcessId} ExitCode={ExitCode}", process.Id, process.ExitCode);
                    if (!_restartExhausted) await RestartAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!client.IsConnected || client.LastHeartbeat is { } last && timeProvider.GetUtcNow() - last > options.HealthPollInterval * 3)
                {
                    if (_state == AgentState.Connected) logger.LogWarning("Audio Agent IPC disconnected or missed three heartbeats.");
                    if (!_restartExhausted) await RestartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RestartForSessionChangeAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _state = AgentState.Reconnecting;
            await StopAgentAsync(cancellationToken).ConfigureAwait(false);
            await RestartUntilConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopAgentAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var processId = _connectedAgentProcessId ?? process?.Id;
        var identity = _connectedIdentity;
        var executablePath = Path.Combine(AppContext.BaseDirectory, "AudioStreamer.AudioAgent.exe");
        _state = AgentState.Disconnected;
        try
        {
            if (client.IsConnected) await client.ShutdownAgentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Audio Agent graceful shutdown request failed. ProcessId={ProcessId}", processId); }

        // An adopted Agent belongs to this audio subsystem too. Retain its verified PID and
        // acquire a handle during orderly Service shutdown so a broken shutdown channel cannot
        // leave AudioAgent.exe running after an MSI uninstall.
        if (process is null && processId is > 0 && identity is not null)
        {
            try
            {
                process = await launcher.AttachAsync(processId.Value, executablePath, identity.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not attach to the adopted Audio Agent for shutdown. ProcessId={ProcessId}", processId);
            }
        }

        if (process is not null && !process.HasExited)
        {
            try
            {
                if (!await process.WaitForExitAsync(options.ShutdownTimeout, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("Audio Agent did not exit within {Timeout}; terminating process. ProcessId={ProcessId}", options.ShutdownTimeout, process.Id);
                    process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Audio Agent process wait failed; terminating process. ProcessId={ProcessId}", process.Id);
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception killException) { logger.LogError(killException, "Could not terminate Audio Agent. ProcessId={ProcessId}", process.Id); }
            }
        }
        if (process is not null)
        {
            logger.LogInformation("Audio Agent stopped. ProcessId={ProcessId} ExitCode={ExitCode}", process.Id, process.ExitCode);
            await process.DisposeAsync().ConfigureAwait(false);
        }
        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _process = null;
        _adopted = false;
        _launched = false;
        _connectedIdentity = null;
        _connectedAgentProcessId = null;
    }
}

/// <summary>Creates the agent in the active Windows session using the service's LocalSystem token privilege.</summary>
public sealed class WindowsAgentProcessLauncher : IAgentProcessLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint TokenAllAccess = 0x000F01FF;
    private const int TokenPrimary = 1;

    public AgentSessionIdentity GetActiveSessionIdentity()
    {
        var sessionId = Native.GetActiveInteractiveSessionId();
        if (!Native.WTSQueryUserToken(sessionId, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed while resolving active Agent identity.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return new AgentSessionIdentity(checked((int)sessionId), identity.User?.Value
                ?? throw new InvalidOperationException("The active interactive session has no user SID."));
    }

    public Task<IAgentProcess> LaunchAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Audio Agent process launch requires Windows.");
        using var tcbPrivilege = Native.EnablePrivilege("SeTcbPrivilege");
        using var assignPrivilege = Native.EnablePrivilege("SeAssignPrimaryTokenPrivilege");
        using var quotaPrivilege = Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
        var sessionId = Native.GetActiveInteractiveSessionId();
        if (!Native.WTSQueryUserToken(sessionId, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
        using (userToken)
        {
            if (!Native.DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, 2, TokenPrimary, out var primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            using (primaryToken)
            {
                var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = "winsta0\\default" };
                var commandLine = new System.Text.StringBuilder($"\"{executablePath}\"");
                if (!Native.CreateEnvironmentBlock(out var environment, primaryToken, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
                try
                {
                    if (!Native.CreateProcessAsUser(primaryToken, executablePath, commandLine, IntPtr.Zero, IntPtr.Zero,
                            false, CreateUnicodeEnvironment | CreateNoWindow, environment, Path.GetDirectoryName(executablePath),
                            ref startup, out var processInfo))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
                    Native.CloseHandle(processInfo.Thread);
                    Native.CloseHandle(processInfo.Process);
                    return Task.FromResult<IAgentProcess>(new OwnedAgentProcess(checked((int)processInfo.ProcessId)));
                }
                finally { Native.DestroyEnvironmentBlock(environment); }
            }
        }
    }

    public Task<IAgentProcess> AttachAsync(int processId, string executablePath, int expectedSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Audio Agent process supervision requires Windows.");
        var process = Process.GetProcessById(processId);
        try
        {
            if (process.HasExited || process.SessionId != expectedSessionId ||
                !string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The adopted process no longer matches the verified Audio Agent identity.");
            return Task.FromResult<IAgentProcess>(new OwnedAgentProcess(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class OwnedAgentProcess : IAgentProcess
    {
        private readonly Process _process;
        public OwnedAgentProcess(int id) : this(Process.GetProcessById(id)) { }
        public OwnedAgentProcess(Process process) => _process = process;
        public int Id => _process.Id;
        public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public int? ExitCode => HasExited ? _process.ExitCode : null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }
        public void Kill() { if (!HasExited) _process.Kill(entireProcessTree: true); }
        public ValueTask DisposeAsync() { _process.Dispose(); return ValueTask.CompletedTask; }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo { public int Size; public string? Reserved; public string? Desktop; public string? Title; public int X; public int Y; public int XSize; public int YSize; public int XCountChars; public int YCountChars; public int FillAttribute; public int Flags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Pointer; public IntPtr StdInput; public IntPtr StdOutput; public IntPtr StdError; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)] private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public LuidAndAttributes Entry; }
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x00000002;
        internal static IDisposable EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
            try
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue failed for {name}.");
                var desired = new TokenPrivileges { Count = 1, Entry = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled } };
                if (!AdjustTokenPrivileges(token, false, ref desired, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"AdjustTokenPrivileges failed for {name}.");
                var error = Marshal.GetLastWin32Error();
                if (error == 1300) throw new Win32Exception(error, $"The Service token does not contain {name}.");
                return new PrivilegeRestore(token, previous);
            }
            catch { token.Dispose(); throw; }
        }
        private sealed class PrivilegeRestore(SafeTokenHandle token, TokenPrivileges previous) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _ = AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _);
                token.Dispose();
            }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeTokenHandle token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(SafeTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, out TokenPrivileges previousState, out uint returnLength);
        [StructLayout(LayoutKind.Sequential)] private struct WtsSessionInfo { public int SessionId; public IntPtr StationName; public int State; }
        internal static uint GetActiveInteractiveSessionId()
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessions failed.");
            try
            {
                var active = new List<uint>();
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessions, index * size));
                    if (entry.State == 0 && entry.SessionId >= 0) active.Add((uint)entry.SessionId);
                }
                var console = WTSGetActiveConsoleSessionId();
                if (console != uint.MaxValue && active.Contains(console)) return console;
                if (active.Count > 0) return active[^1];
                throw new InvalidOperationException("No active interactive user session is available.");
            }
            finally { WTSFreeMemory(sessions); }
        }
        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out SafeTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(SafeTokenHandle existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out SafeTokenHandle newToken);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUser(SafeTokenHandle token, string applicationName, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
    }

    private sealed class SafeTokenHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
}


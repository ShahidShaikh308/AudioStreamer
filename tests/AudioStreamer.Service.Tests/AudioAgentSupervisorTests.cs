using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.Service;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Xunit;

namespace AudioStreamer.Service.Tests;

public sealed class AudioAgentSupervisorTests
{
    [Fact]
    public async Task Startup_LaunchesAgentAndCompletesHandshakeBeforeConnected()
    {
        var fixture = new Fixture();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        Assert.Equal(1, fixture.Client.HandshakeCount);
        Assert.Equal(AgentState.Connected, fixture.Supervisor.Status.AgentState);
        Assert.True(fixture.Supervisor.Status.Launched);
        Assert.False(fixture.Supervisor.Status.Adopted);
        Assert.True(fixture.Supervisor.Status.AgentConnected);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_VersionMismatchReportsAgentUnavailable()
    {
        var fixture = new Fixture(version: AgentProtocol.CurrentVersion + 1);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(AgentState.Failed, fixture.Supervisor.Status.AgentState);
        Assert.Equal("AgentUnavailable", fixture.Supervisor.Status.ErrorCode);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_AdoptsSurvivingAgentWithoutLaunchingAnother()
    {
        var fixture = new Fixture(existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Supervisor.Status.Adopted);
        Assert.False(fixture.Supervisor.Status.Launched);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServiceRestart_AdoptsAgentLeftByPreviousService()
    {
        var fixture = new Fixture(existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(AgentState.Connected, fixture.Supervisor.Status.AgentState);
        Assert.Equal(42, fixture.Client.LastReadyProcessId);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateLaunchRace_AdoptsMutexOwner()
    {
        var fixture = new Fixture(existingAfterLaunchPid: 1, exitDuplicateLaunch: true);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Launcher.Processes[0].HasExited);
        Assert.True(fixture.Supervisor.Status.Adopted);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AgentSurvivesServiceCrash_NextSupervisorAdoptsIt()
    {
        var fixture = new Fixture(existingAgentPid: 91);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(91, fixture.Client.LastReadyProcessId);
        Assert.True(fixture.Supervisor.Status.Adopted);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleAgentSession_IsRejected()
    {
        var fixture = new Fixture(existingAgentPid: 42, agentSessionId: 2);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(AgentState.Failed, fixture.Supervisor.Status.AgentState);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExistingAgentProtocolMismatch_IsRejectedWithoutLaunchingDuplicate()
    {
        var fixture = new Fixture(version: AgentProtocol.CurrentVersion + 1, existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(AgentState.Failed, fixture.Supervisor.Status.AgentState);
        Assert.Equal(0, fixture.Launcher.LaunchCount);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ActiveUserSessionChange_RestartsAgentConnectionForNewSession()
    {
        var fixture = new Fixture(existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Launcher.Identity = new AgentSessionIdentity(2, "S-1-5-21-next-user");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Logger.WaitForSuccessfulHandshakeAsync(2);
        Assert.Equal(AgentState.Connected, fixture.Supervisor.Status.AgentState);
        Assert.Equal(2, fixture.Client.LastReadySessionId);
        Assert.True(fixture.Client.ShutdownCount > 0);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProcessCrash_RestartsAgentAndReconnects()
    {
        var fixture = new Fixture();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Launcher.Processes[0].Exit(17);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Logger.WaitForSuccessfulHandshakeAsync(2);
        Assert.Equal(2, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Client.IsConnected);
        Assert.Equal(1, fixture.Supervisor.Status.RestartCount);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HeartbeatDisconnect_RestartsAgent()
    {
        var fixture = new Fixture();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Client.Disconnect();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Logger.WaitForSuccessfulHandshakeAsync(2);
        Assert.Equal(2, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Launcher.Processes[0].HasExited);
        Assert.True(fixture.Client.IsConnected);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IpcDisconnectWithLiveAgent_TerminatesVerifiedProcessAndRelaunches()
    {
        var fixture = new Fixture();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        var disconnectedProcessId = fixture.Client.LastReadyProcessId;

        fixture.Client.Disconnect();
        await fixture.Supervisor.RestartAsync();

        Assert.Equal(2, fixture.Launcher.LaunchCount);
        Assert.Equal(disconnectedProcessId, fixture.Launcher.Processes[0].Id);
        Assert.True(fixture.Launcher.Processes[0].HasExited);
        Assert.True(fixture.Client.IsConnected);
        Assert.Equal(AgentState.Connected, fixture.Supervisor.Status.AgentState);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IpcDisconnectWithLiveAdoptedAgent_AttachesToVerifiedProcessAndRelaunches()
    {
        var fixture = new Fixture(existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);

        fixture.Client.Disconnect();
        await fixture.Supervisor.RestartAsync();

        Assert.Equal(1, fixture.Launcher.AttachCount);
        Assert.Equal(1, fixture.Launcher.AttachedProcesses[42].KillCount);
        Assert.True(fixture.Launcher.AttachedProcesses[42].HasExited);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Supervisor.Status.Launched);
        Assert.True(fixture.Client.IsConnected);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThreeMissedHeartbeats_RestartAgent()
    {
        var fixture = new Fixture();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Client.SetHeartbeat(fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(4));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Logger.WaitForSuccessfulHandshakeAsync(2);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        Assert.True(fixture.Client.IsConnected);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualStop_SendsGracefulShutdownAndExitsProcess()
    {
        var fixture = new Fixture();
        fixture.Client.OnShutdown = () => fixture.Launcher.Processes[^1].Exit(0);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Client.ShutdownCount);
        Assert.True(fixture.Launcher.Processes[0].HasExited);
        Assert.Equal(AgentState.Disconnected, fixture.Supervisor.Status.AgentState);
    }

    [Fact]
    public async Task ManualStop_ForceStopsAdoptedAgentWhenShutdownChannelFails()
    {
        var fixture = new Fixture(existingAgentPid: 42);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Client.OnShutdown = () => throw new IOException("Injected shutdown-channel failure.");

        await fixture.Supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Launcher.AttachCount);
        Assert.True(fixture.Launcher.AttachedProcesses[42].HasExited);
        Assert.Equal(1, fixture.Launcher.AttachedProcesses[42].KillCount);
    }

    [Fact]
    public async Task Restart_StopsAfterMaximumAttempts()
    {
        var fixture = new Fixture(maximumRestarts: 2, failLaunchAfterFirst: true);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        fixture.Launcher.Processes[0].Exit(1);
        await fixture.Supervisor.RestartAsync();
        Assert.Equal(2, fixture.Supervisor.Status.RestartCount);
        Assert.Equal(AgentState.Failed, fixture.Supervisor.Status.AgentState);
        Assert.Equal("AgentUnavailable", fixture.Supervisor.Status.ErrorCode);
        await fixture.Supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupCancellation_PropagatesAndDoesNotStartMonitoring()
    {
        var fixture = new Fixture(blockLaunch: true);
        using var cancellation = new CancellationTokenSource();
        var starting = fixture.Supervisor.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(0, fixture.Launcher.LaunchCount);
    }

    private sealed class Fixture
    {
        public ManualTimeProvider Clock { get; }
        public FakeClient Client { get; }
        public FakeLauncher Launcher { get; }
        public HandshakeRecordingLogger Logger { get; } = new();
        public AudioAgentSupervisor Supervisor { get; }

        public Fixture(int version = AgentProtocol.CurrentVersion, int maximumRestarts = 5,
            bool failLaunchAfterFirst = false, bool blockLaunch = false, int? existingAgentPid = null,
            int? existingAfterLaunchPid = null, bool exitDuplicateLaunch = false, int agentSessionId = 1)
        {
            Clock = new ManualTimeProvider();
            Launcher = new FakeLauncher(failLaunchAfterFirst, blockLaunch, exitDuplicateLaunch);
            Client = new FakeClient(version, () => existingAgentPid is not null && Launcher.LaunchCount == 0
                    ? existingAgentPid.Value
                    : Launcher.LaunchCount > 0 ? existingAfterLaunchPid ?? Launcher.Processes[^1].Id : 0,
                () => existingAgentPid is not null && Launcher.LaunchCount == 0 || Launcher.LaunchCount > 0 &&
                    (existingAfterLaunchPid is not null || !Launcher.Processes[^1].HasExited), () => agentSessionId == 1 ? Launcher.Identity.SessionId : agentSessionId);
            Supervisor = new AudioAgentSupervisor(Client, Launcher,
                new AudioAgentSupervisorOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.Zero,
                    TimeSpan.Zero, TimeSpan.FromSeconds(1), maximumRestarts),
                Logger, Clock);
        }
    }

    private sealed class HandshakeRecordingLogger : ILogger<AudioAgentSupervisor>
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _handshakes = new();
        private int _successfulHandshakeCount;

        public Task WaitForSuccessfulHandshakeAsync(int count)
        {
            var completion = _handshakes.GetOrAdd(count,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Volatile.Read(ref _successfulHandshakeCount) >= count)
                completion.TrySetResult();
            return completion.Task;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("handshake successful", StringComparison.OrdinalIgnoreCase))
                return;

            var count = Interlocked.Increment(ref _successfulHandshakeCount);
            if (_handshakes.TryGetValue(count, out var completion))
                completion.TrySetResult();
        }
    }

    private sealed class FakeClient(int version, Func<int> processId, Func<bool> reachable, Func<int> sessionId) : IAudioAgentClient
    {
        private bool _connected;
        private int? _unreachableProcessId;
        public int HandshakeCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int LastReadyProcessId { get; private set; }
        public int LastReadySessionId { get; private set; }
        public DateTimeOffset? LastHeartbeat { get; private set; }
        public Action? OnShutdown { get; set; }
        public bool IsConnected => _connected;
        public ValueTask ConnectAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); if (!reachable() || processId() == _unreachableProcessId) throw new IOException("No Agent is listening."); _connected = true; LastHeartbeat = DateTimeOffset.UtcNow; return ValueTask.CompletedTask; }
        public ValueTask<AgentReady> HandshakeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HandshakeCount++;
            LastReadyProcessId = processId();
            LastReadySessionId = sessionId();
            var sid = LastReadySessionId == 1 ? "S-1-5-21-test" : "S-1-5-21-next-user";
            return ValueTask.FromResult(new AgentReady(version, Guid.NewGuid(), "test", LastReadySessionId, sid, LastReadyProcessId));
        }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) { _connected = false; return ValueTask.CompletedTask; }
        public Task StartMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ShutdownAgentAsync(CancellationToken cancellationToken) { ShutdownCount++; OnShutdown?.Invoke(); return ValueTask.CompletedTask; }
        public void Disconnect() { _connected = false; _unreachableProcessId = LastReadyProcessId; }
        public void SetHeartbeat(DateTimeOffset value) => LastHeartbeat = value;
    }

    private sealed class FakeLauncher(bool failAfterFirst, bool block, bool exitDuplicate) : IAgentProcessLauncher
    {
        public List<FakeProcess> Processes { get; } = [];
        public Dictionary<int, FakeProcess> AttachedProcesses { get; } = [];
        public TaskCompletionSource SecondLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LaunchCount => Processes.Count;
        public int AttachCount { get; private set; }
        public AgentSessionIdentity Identity { get; set; } = new(1, "S-1-5-21-test");
        public AgentSessionIdentity GetActiveSessionIdentity() => Identity;
        public async Task<IAgentProcess> LaunchAsync(string executablePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (failAfterFirst && Processes.Count > 0) throw new InvalidOperationException("Injected launch failure.");
            var process = new FakeProcess(Processes.Count + (exitDuplicate ? 2 : 1));
            Processes.Add(process);
            if (exitDuplicate) process.Exit(0);
            if (Processes.Count == 2) SecondLaunch.TrySetResult();
            return process;
        }

        public Task<IAgentProcess> AttachAsync(int processId, string executablePath, int expectedSessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttachCount++;
            if (!AttachedProcesses.TryGetValue(processId, out var process))
                AttachedProcesses.Add(processId, process = new FakeProcess(processId));
            return Task.FromResult<IAgentProcess>(process);
        }
    }

    private sealed class FakeProcess(int id) : IAgentProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; } = id;
        public int KillCount { get; private set; }
        public bool HasExited => _exit.Task.IsCompleted;
        public int? ExitCode => HasExited ? _exit.Task.Result : null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try { await _exit.Task.WaitAsync(timeout, cancellationToken); return true; }
            catch (TimeoutException) { return false; }
        }
        public void Kill() { KillCount++; Exit(-1); }
        public void Exit(int code) => _exit.TrySetResult(code);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long GetTimestamp() { lock (_sync) return _timestamp; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            Change(timer, dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _now += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers.ToArray())
                    if (!timer.Disposed && timer.Due <= _timestamp)
                    {
                        callbacks.Add((timer.Callback, timer.State));
                        timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _timestamp + timer.Period.Ticks;
                    }
            }
            foreach (var (callback, state) in callbacks) callback(state);
        }
        private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                if (!_timers.Contains(timer)) _timers.Add(timer);
                timer.Period = period;
                timer.Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _timestamp + dueTime.Ticks;
            }
        }
        private void Remove(ManualTimer timer) { lock (_sync) { timer.Disposed = true; _timers.Remove(timer); } }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; set; }
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { owner.Change(this, dueTime, period); return true; }
            public void Dispose() => owner.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

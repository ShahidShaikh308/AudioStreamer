using AudioStreamer.Engine.Configuration;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;
using System.Diagnostics;

namespace AudioStreamer.Engine.Managers;

/// <summary>Owns canonical versioned configuration, validation, revision conflicts, persistence, and rollback.</summary>
internal sealed class SettingsManager(IConfigurationStore store, string defaultNodeName, EngineEventBus events)
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private EngineSettings? _current;

    public EngineSettings Current => Volatile.Read(ref _current) ??
        throw new InvalidOperationException("Settings have not been loaded.");

    public async Task<EngineResult<EngineSettings>> LoadAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EngineSettings settings;
            try
            {
                var stored = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                var isNew = stored is null;
                settings = stored ?? EngineSettings.CreateDefault(defaultNodeName);
                if (!isNew && string.Equals(settings.NodeName, "AudioStreamer Node", StringComparison.Ordinal) &&
                    !defaultNodeName.Equals("AudioStreamer Node", StringComparison.Ordinal))
                {
                    settings = settings with { Revision = checked(settings.Revision + 1), NodeName = defaultNodeName };
                    await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                if (!settings.IsValid)
                    return EngineResult<EngineSettings>.Failure(EngineErrorCode.InvalidArgument, "configuration",
                        "Stored settings are invalid or use an unsupported schema.");
                if (isNew)
                    await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Trace.TraceError("Settings load failed: {0}", exception);
                return EngineResult<EngineSettings>.Failure(EngineErrorCode.PersistenceFailure, "configuration",
                    "Settings could not be loaded or initialized.", true);
            }

            Volatile.Write(ref _current, settings);
            await PublishAsync(settings, status).ConfigureAwait(false);
            return EngineResult<EngineSettings>.Success(settings);
        }
        finally { _writer.Release(); }
    }

    public async Task<EngineResult<SettingsSnapshot>> UpdateAsync(SettingsPatch patch, EngineStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current;
            if (patch.ExpectedRevision != current.Revision)
                return EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.ConfigurationConflict, "configuration",
                    "Settings changed since the supplied revision.", true);

            var name = patch.NodeName is null ? current.NodeName : patch.NodeName.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                return EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.InvalidArgument, "configuration",
                    "Node name must contain 1 to 80 characters.");
            var candidate = current with { Revision = checked(current.Revision + 1), NodeName = name };
            if (!candidate.IsValid)
                return EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.InvalidArgument, "configuration", "Settings are invalid.");

            try
            {
                await store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RollbackAsync(current).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Settings persistence failed: {0}", exception);
                var rollbackSucceeded = await RollbackAsync(current).ConfigureAwait(false);
                var message = rollbackSucceeded
                    ? "Settings could not be saved; the previous configuration was restored."
                    : "Settings could not be saved or restored; configuration requires repair.";
                return EngineResult<SettingsSnapshot>.Failure(EngineErrorCode.PersistenceFailure, "configuration", message, true);
            }

            // After a successful atomic persistence, complete the in-memory commit even if cancellation arrives.
            Volatile.Write(ref _current, candidate);
            await PublishAsync(candidate, status).ConfigureAwait(false);
            return EngineResult<SettingsSnapshot>.Success(ToSnapshot(candidate));
        }
        finally { _writer.Release(); }
    }

    private async Task<bool> RollbackAsync(EngineSettings current)
    {
        try { await store.SaveAsync(current, CancellationToken.None).ConfigureAwait(false); return true; }
        catch (Exception exception) { Trace.TraceError("Settings rollback failed: {0}", exception); return false; }
    }

    private async ValueTask PublishAsync(
    EngineSettings settings,
    EngineStatus status)
    {
        await events.PublishAsync(
            "settings",
            "ConfigurationChanged",
            status,
            new SettingsChangedPayload(settings),
            CancellationToken.None);
    }

    private static SettingsSnapshot ToSnapshot(EngineSettings settings) =>
        new(settings.SchemaVersion, settings.Revision, settings.NodeId, settings.NodeName);
}

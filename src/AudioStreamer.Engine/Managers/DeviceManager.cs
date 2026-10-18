using System.Collections.Immutable;
using AudioStreamer.Engine.Contracts;
using AudioStreamer.Engine.Runtime;

namespace AudioStreamer.Engine.Managers;

internal sealed class DeviceManager(IAudioDeviceCatalog catalog, EngineEventBus events)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AudioDeviceInfo[] _devices = [];

    public ImmutableArray<AudioDeviceInfo> Current => ImmutableArray.CreateRange(Volatile.Read(ref _devices));

    public async Task<EngineResult<ImmutableArray<AudioDeviceSnapshot>>> EnumerateAsync(EngineStatus status,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await catalog.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return EngineResult<ImmutableArray<AudioDeviceSnapshot>>.Failure(
                result.Error!.Code, result.Error.Subsystem, result.Error.Message, result.Error.IsRetryable);
            if (!Validate(result.Value!))
                return EngineResult<ImmutableArray<AudioDeviceSnapshot>>.Failure(EngineErrorCode.InternalFailure,
                    "devices", "Device catalog returned invalid endpoint metadata.");
            var next = result.Value!;
            Volatile.Write(ref _devices, next.ToArray());
            var snapshots = next.Select(device => new AudioDeviceSnapshot(device.Id, device.Name, device.Direction)).ToImmutableArray();
            await events.PublishAsync("devices", "DeviceListChanged", status,
                new DevicesChangedPayload(snapshots), cancellationToken).ConfigureAwait(false);
            return EngineResult<ImmutableArray<AudioDeviceSnapshot>>.Success(snapshots);
        }
        finally { _refreshGate.Release(); }
    }

    public async Task<EngineResult<OperationReceipt>> SelectAsync(AudioDeviceSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var selected in new[] { selection.CaptureDeviceId, selection.PlaybackDeviceId }.Where(id => id is not null))
            if (string.IsNullOrWhiteSpace(selected) || selected.Length > 256 ||
                !Current.Any(device => device.Id == selected && device.IsAvailable))
                return EngineResult<OperationReceipt>.Failure(EngineErrorCode.NotFound, "devices", "Selected audio endpoint is unavailable.");
        var result = await catalog.SelectAsync(selection, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value == true
            ? EngineResult<OperationReceipt>.Success(new OperationReceipt(Guid.NewGuid(), "Applied"))
            : EngineResult<OperationReceipt>.Failure(result.IsSuccess ? EngineErrorCode.InternalFailure : result.Error!.Code,
                result.IsSuccess ? "devices" : result.Error!.Subsystem,
                result.IsSuccess ? "Platform catalog did not apply the device selection." : result.Error!.Message,
                !result.IsSuccess && result.Error!.IsRetryable);
    }

    private static bool Validate(ImmutableArray<AudioDeviceInfo> devices) => !devices.IsDefault && devices.Length <= 256 &&
        devices.All(device => !string.IsNullOrWhiteSpace(device.Id) && device.Id.Length <= 256 &&
                              !string.IsNullOrWhiteSpace(device.Name) && device.Name.Length <= 256 &&
                              device.Direction is "capture" or "playback");
}

internal sealed class UnavailableAudioDeviceCatalog : IAudioDeviceCatalog
{
    public ValueTask<EngineResult<ImmutableArray<AudioDeviceInfo>>> EnumerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EngineResult<ImmutableArray<AudioDeviceInfo>>.Failure(
            EngineErrorCode.CapabilityUnavailable, "devices", "No platform audio-device catalog is registered."));
    }

    public ValueTask<EngineResult<bool>> SelectAsync(AudioDeviceSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EngineResult<bool>.Failure(EngineErrorCode.CapabilityUnavailable,
            "devices", "No platform audio-device catalog is registered."));
    }
}

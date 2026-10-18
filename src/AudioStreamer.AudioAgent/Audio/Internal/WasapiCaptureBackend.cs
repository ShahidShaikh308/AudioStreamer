using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer.AudioAgent.Audio.Internal;

internal delegate void CapturePacketHandler(ReadOnlySpan<byte> data, bool isSilent, long timestampTicks);

internal interface IWasapiCaptureSession : IAsyncDisposable
{
    string? DeviceId { get; }
    event CapturePacketHandler? PacketAvailable;
    void Start();
    void Pause();
}

internal interface IWasapiCaptureFactory
{
    IWasapiCaptureSession Create(AudioCaptureOptions options);
}

internal sealed class NAudioWasapiCaptureFactory : IWasapiCaptureFactory
{
    public IWasapiCaptureSession Create(AudioCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var enumerator = new MMDeviceEnumerator();
        var expectedFlow = options.Source == AudioCaptureSource.Loopback
            ? DataFlow.Render
            : DataFlow.Capture;
        MMDevice? device = options.DeviceId is { Length: > 0 } deviceId
            ? enumerator.GetDevice(deviceId)
            : enumerator.GetDefaultAudioEndpoint(expectedFlow, Role.Multimedia);

        WasapiRecorder? recorder = null;
        try
        {
            if (device.DataFlow != expectedFlow)
            {
                throw new ArgumentException(
                    "Selected endpoint does not match the requested capture source.", nameof(options));
            }

            var format = WasapiAudioCapture.ToWaveFormat(options.Format);
            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithFormat(format);

            // NAudio's loopback implementation uses polling because shared-mode loopback does not
            // reliably signal event callbacks. Microphone capture uses event-driven synchronization.
            builder = options.Source == AudioCaptureSource.Loopback
                ? builder.WithLoopbackCapture().WithPollingSync()
                : builder.WithEventSync();

            recorder = builder.Build();
            var session = new Session(device, recorder);
            recorder = null;
            device = null;
            return session;
        }
        catch
        {
            recorder?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    private sealed class Session : IWasapiCaptureSession
    {
        private readonly MMDevice _device;
        private readonly WasapiRecorder _recorder;
        private int _disposed;

        public Session(MMDevice device, WasapiRecorder recorder)
        {
            _device = device;
            _recorder = recorder;
            _recorder.DataAvailable += OnDataAvailable;
        }

        public string? DeviceId => _device.ID;
        public event CapturePacketHandler? PacketAvailable;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _recorder.StartRecording();
        }

        public void Pause()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _recorder.StopRecording();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _recorder.DataAvailable -= OnDataAvailable;
            try
            {
                await _recorder.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _device.Dispose();
            }
        }

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags,
            long devicePosition, long qpcPosition)
        {
            _ = devicePosition;
            PacketAvailable?.Invoke(buffer, (flags & AudioClientBufferFlags.Silent) != 0, qpcPosition);
        }
    }
}

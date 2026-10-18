using AudioStreamer.AudioAgent.Contracts;
using AudioStreamer.AudioAgent.Contracts.Audio;
using AudioStreamer.AudioAgent.Audio.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer.AudioAgent.Audio.Internal;

internal interface IPcmAudioSource { AudioFormat Format { get; } int Read(Span<byte> destination); }
internal interface IWasapiPlaybackSession : IAsyncDisposable
{
    TimeSpan CurrentLatency { get; }
    void Start();
    void Pause();
}
internal interface IWasapiPlaybackFactory { IWasapiPlaybackSession Create(AudioPlaybackOptions options, IPcmAudioSource source); }

internal sealed class NAudioWasapiPlaybackFactory : IWasapiPlaybackFactory
{
    public IWasapiPlaybackSession Create(AudioPlaybackOptions options, IPcmAudioSource source) => new Session(options, source);

    private sealed class Session : IWasapiPlaybackSession
    {
        private readonly MMDeviceEnumerator _enumerator;
        private readonly MMDevice _device;
        private readonly WasapiPlayer _player;
        private bool _disposed;

        public Session(AudioPlaybackOptions options, IPcmAudioSource source)
        {
            _enumerator = new MMDeviceEnumerator();
            _device = options.DeviceId is { Length: > 0 } id
                ? _enumerator.GetDevice(id)
                : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var dataFlow = _device.DataFlow;
            if (dataFlow != DataFlow.Render)
            {
                _device.Dispose();
                _enumerator.Dispose();
                throw new ArgumentException("Selected endpoint is not a render device.", nameof(options));
            }
            var builder = new WasapiPlayerBuilder()
                .WithDevice(_device)
                .WithEventSync()
                .WithLatency(options.RequestedLatencyMilliseconds)
                .WithMmcssThreadPriority("Pro Audio");
            _player = builder.Build();
            _player.Init(new PcmWaveProvider(source));
        }

        public TimeSpan CurrentLatency => _player.CurrentLatency;
        public void Start() => _player.Play();
        public void Pause() => _player.Pause();

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { _player.Stop(); } catch (InvalidOperationException) { }
            await _player.DisposeAsync().ConfigureAwait(false);
            _device.Dispose();
            _enumerator.Dispose();
        }

        private sealed class PcmWaveProvider(IPcmAudioSource source) : IWaveProvider
        {
            public WaveFormat WaveFormat { get; } = WasapiAudioCapture.ToWaveFormat(source.Format);
            public int Read(Span<byte> destination) => source.Read(destination);
        }
    }
}

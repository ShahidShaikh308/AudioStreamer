using System.Buffers.Binary;
using AudioStreamer.Engine.Contracts;

namespace AudioStreamer.Platform.Windows.Audio.Formats;

/// <summary>Allocation-free PCM sample conversion, channel mapping and linear resampling.</summary>
public sealed class PcmFormatConverter : IAudioFormatConverter
{
    public int Convert(ReadOnlySpan<byte> source, AudioFormat sourceFormat, Span<byte> destination, AudioFormat destinationFormat)
    {
        sourceFormat.EnsureSupported();
        destinationFormat.EnsureSupported();
        if (source.Length % sourceFormat.BytesPerFrame != 0) throw new ArgumentException("Source PCM is not frame aligned.", nameof(source));
        var sourceFrames = source.Length / sourceFormat.BytesPerFrame;
        if (sourceFrames == 0) return 0;
        var outputFrames = checked((int)Math.Min(int.MaxValue,
            Math.Round((double)sourceFrames * destinationFormat.SampleRate / sourceFormat.SampleRate, MidpointRounding.AwayFromZero)));
        if (outputFrames * (long)destinationFormat.BytesPerFrame > destination.Length)
            throw new ArgumentException("Destination does not have enough space for converted PCM.", nameof(destination));

        for (var outputFrame = 0; outputFrame < outputFrames; outputFrame++)
        {
            var sourcePosition = (double)outputFrame * sourceFormat.SampleRate / destinationFormat.SampleRate;
            var first = Math.Min((int)sourcePosition, sourceFrames - 1);
            var second = Math.Min(first + 1, sourceFrames - 1);
            var fraction = (float)(sourcePosition - first);
            for (var channel = 0; channel < destinationFormat.Channels; channel++)
            {
                var sourceChannel = sourceFormat.Channels == 1 ? 0 :
                    destinationFormat.Channels == 1 ? -1 : channel;
                var a = ReadMappedSample(source, sourceFormat, first, sourceChannel);
                var b = ReadMappedSample(source, sourceFormat, second, sourceChannel);
                WriteSample(destination, destinationFormat, outputFrame, channel, a + ((b - a) * fraction));
            }
        }
        return outputFrames;
    }

    private static float ReadMappedSample(ReadOnlySpan<byte> source, AudioFormat format, int frame, int channel)
    {
        if (channel < 0)
            return (ReadSample(source, format, frame, 0) + ReadSample(source, format, frame, 1)) * 0.5f;
        return ReadSample(source, format, frame, channel);
    }

    private static float ReadSample(ReadOnlySpan<byte> source, AudioFormat format, int frame, int channel)
    {
        var offset = checked((frame * format.Channels + channel) * format.BytesPerSample);
        return format.SampleFormat == PcmSampleFormat.Float32
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)))
            : BinaryPrimitives.ReadInt16LittleEndian(source.Slice(offset, 2)) / 32768f;
    }

    private static void WriteSample(Span<byte> destination, AudioFormat format, int frame, int channel, float sample)
    {
        var offset = checked((frame * format.Channels + channel) * format.BytesPerSample);
        sample = Math.Clamp(sample, -1f, 1f);
        if (format.SampleFormat == PcmSampleFormat.Float32)
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), BitConverter.SingleToInt32Bits(sample));
        else
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(offset, 2), (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue));
    }
}

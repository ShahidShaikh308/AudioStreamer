namespace AudioStreamer.Audio.Transport.Protocol;

/// <summary>Yields MTU-safe, stereo-float frame-aligned slices without copying PCM.</summary>
public static class AudioProtocolV3Packetizer
{
    public static IEnumerable<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.Length == 0 || pcm.Length % AudioProtocolV3Codec.StereoFloat32FrameSize != 0)
            throw new AudioProtocolException("PCM must contain non-empty complete stereo float32 frames.");
        var payloadSize = AudioProtocolV3Codec.MaximumPayloadSize / AudioProtocolV3Codec.StereoFloat32FrameSize * AudioProtocolV3Codec.StereoFloat32FrameSize;
        for (var offset = 0; offset < pcm.Length; offset += payloadSize)
            yield return pcm.Slice(offset, Math.Min(payloadSize, pcm.Length - offset));
    }
}

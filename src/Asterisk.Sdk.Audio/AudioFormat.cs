namespace Asterisk.Sdk.Audio;

/// <summary>Describes an audio stream's format. Immutable value type.</summary>
public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    AudioEncoding Encoding)
{
    // ── Common telephony formats ──

    /// <summary>Signed 16-bit linear PCM, mono, 8 kHz (Asterisk native).</summary>
    public static readonly AudioFormat Slin16Mono8kHz = new(8000, 1, 16, AudioEncoding.LinearPcm);

    /// <summary>Signed 16-bit linear PCM, mono, 16 kHz (wideband).</summary>
    public static readonly AudioFormat Slin16Mono16kHz = new(16000, 1, 16, AudioEncoding.LinearPcm);

    /// <summary>Signed 16-bit linear PCM, mono, 24 kHz.</summary>
    public static readonly AudioFormat Slin16Mono24kHz = new(24000, 1, 16, AudioEncoding.LinearPcm);

    /// <summary>Signed 16-bit linear PCM, mono, 48 kHz.</summary>
    public static readonly AudioFormat Slin16Mono48kHz = new(48000, 1, 16, AudioEncoding.LinearPcm);

    /// <summary>32-bit float, mono, 16 kHz (AI model input format).</summary>
    public static readonly AudioFormat Float32Mono16kHz = new(16000, 1, 32, AudioEncoding.IeeeFloat);

    /// <summary>Bytes per sample across all channels.</summary>
    public int BytesPerSample => Channels * (BitsPerSample / 8);

    /// <summary>Byte count for an audio frame of the given duration.</summary>
    public int BytesPerFrame(TimeSpan frameDuration) =>
        (int)(SampleRate * BytesPerSample * frameDuration.TotalSeconds);

    /// <summary>Number of samples per frame of the given duration.</summary>
    public int SamplesPerFrame(TimeSpan frameDuration) =>
        (int)(SampleRate * frameDuration.TotalSeconds);
}

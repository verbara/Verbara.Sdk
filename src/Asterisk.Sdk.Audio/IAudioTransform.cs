namespace Asterisk.Sdk.Audio;

/// <summary>
/// Chainable audio processing transform.
/// Operates on raw bytes to support both PCM16 and float32 in the same pipeline.
/// </summary>
public interface IAudioTransform
{
    /// <summary>Expected input audio format.</summary>
    AudioFormat InputFormat { get; }

    /// <summary>Produced output audio format.</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>
    /// Process audio data. Returns the number of bytes written to <paramref name="output"/>.
    /// </summary>
    int Process(ReadOnlySpan<byte> input, Span<byte> output);
}

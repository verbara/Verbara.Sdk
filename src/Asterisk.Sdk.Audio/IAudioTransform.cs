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
    /// Returns the maximum number of bytes that may be written to the output buffer
    /// for a given number of input bytes. Callers must allocate at least this many bytes.
    /// </summary>
    int MaxOutputBytes(int inputBytes);

    /// <summary>
    /// Process audio data. Returns the number of bytes written to <paramref name="output"/>.
    /// The <paramref name="output"/> buffer must be at least <see cref="MaxOutputBytes"/> bytes.
    /// </summary>
    int Process(ReadOnlySpan<byte> input, Span<byte> output);
}

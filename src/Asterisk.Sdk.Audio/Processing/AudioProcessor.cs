namespace Asterisk.Sdk.Audio.Processing;

/// <summary>Static audio processing utilities. Zero-alloc, AOT-safe.</summary>
public static class AudioProcessor
{
    /// <summary>Apply gain in dB to PCM16 samples in-place. Clamps to <see cref="short"/> range.</summary>
    /// <param name="samples">PCM16 samples to modify in place.</param>
    /// <param name="gainDb">Gain in decibels (positive = amplify, negative = attenuate).</param>
    public static void ApplyGain(Span<short> samples, float gainDb)
    {
        float linear = MathF.Pow(10.0f, gainDb / 20.0f);
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i] * linear;
            if (s > short.MaxValue) s = short.MaxValue;
            else if (s < short.MinValue) s = short.MinValue;
            samples[i] = (short)s;
        }
    }

    /// <summary>
    /// Convert PCM16 samples to normalized float32 (-1.0 to 1.0).
    /// </summary>
    /// <param name="pcm16">Input PCM16 samples.</param>
    /// <param name="destination">Output float32 buffer. Must be at least as long as <paramref name="pcm16"/>.</param>
    public static void ConvertToFloat32(ReadOnlySpan<short> pcm16, Span<float> destination)
    {
        if (destination.Length < pcm16.Length)
            throw new ArgumentException("Output buffer too small.", nameof(destination));

        for (int i = 0; i < pcm16.Length; i++)
            destination[i] = pcm16[i] / 32768.0f;
    }

    /// <summary>
    /// Convert normalized float32 (-1.0 to 1.0) to PCM16 samples. Clamps out-of-range values.
    /// </summary>
    /// <param name="source">Input float32 samples.</param>
    /// <param name="destination">Output PCM16 buffer. Must be at least as long as <paramref name="source"/>.</param>
    public static void ConvertToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Output buffer too small.", nameof(destination));

        for (int i = 0; i < source.Length; i++)
        {
            float s = source[i] * 32767.0f;
            if (s > short.MaxValue) s = short.MaxValue;
            else if (s < short.MinValue) s = short.MinValue;
            destination[i] = (short)s;
        }
    }

    /// <summary>
    /// Calculate the RMS energy of PCM16 samples.
    /// Returns 0.0 for empty input.
    /// </summary>
    /// <param name="samples">PCM16 samples.</param>
    /// <returns>RMS energy value (0.0 to ~32767.0).</returns>
    public static double CalculateRmsEnergy(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0.0;

        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sum += (double)samples[i] * samples[i];

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Returns true if the audio frame is below the silence threshold.
    /// </summary>
    /// <param name="samples">PCM16 samples.</param>
    /// <param name="thresholdDb">Energy threshold in dBFS. Default: -40 dBFS.</param>
    /// <returns>True if the frame is silence.</returns>
    public static bool IsSilence(ReadOnlySpan<short> samples, double thresholdDb = -40.0)
    {
        double rms = CalculateRmsEnergy(samples);
        if (rms <= 0.0) return true;

        // Convert RMS to dBFS: 20 * log10(rms / 32768.0)
        double dbfs = 20.0 * Math.Log10(rms / 32768.0);
        return dbfs < thresholdDb;
    }
}

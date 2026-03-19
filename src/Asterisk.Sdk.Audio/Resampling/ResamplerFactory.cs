namespace Asterisk.Sdk.Audio.Resampling;

/// <summary>Factory for creating pre-configured polyphase resamplers for telephony rate pairs.</summary>
public static class ResamplerFactory
{
    private static readonly (int InputRate, int OutputRate, int L, int M, float[] Coefficients)[] Supported =
    [
        (8000, 16000, 2, 1, ResamplerCoefficients.Coefficients8To16k),
        (16000, 8000, 1, 2, ResamplerCoefficients.Coefficients16To8k),
        (8000, 24000, 3, 1, ResamplerCoefficients.Coefficients8To24k),
        (24000, 8000, 1, 3, ResamplerCoefficients.Coefficients24To8k),
        (16000, 24000, 3, 2, ResamplerCoefficients.Coefficients16To24k),
        (24000, 16000, 2, 3, ResamplerCoefficients.Coefficients24To16k),
        (8000, 48000, 6, 1, ResamplerCoefficients.Coefficients8To48k),
        (48000, 8000, 1, 6, ResamplerCoefficients.Coefficients48To8k),
        (16000, 48000, 3, 1, ResamplerCoefficients.Coefficients16To48k),
        (48000, 16000, 1, 3, ResamplerCoefficients.Coefficients48To16k),
        (24000, 48000, 2, 1, ResamplerCoefficients.Coefficients24To48k),
        (48000, 24000, 1, 2, ResamplerCoefficients.Coefficients48To24k),
    ];

    /// <summary>
    /// Creates a resampler for the given rate pair.
    /// Each instance is stateful (delay line) and must be used for a single audio stream.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the rate pair is not supported.</exception>
    public static PolyphaseResampler Create(int inputRate, int outputRate)
    {
        foreach (var entry in Supported)
        {
            if (entry.InputRate == inputRate && entry.OutputRate == outputRate)
                return new PolyphaseResampler(inputRate, outputRate, entry.L, entry.M, entry.Coefficients);
        }

        throw new ArgumentException(
            $"Rate pair {inputRate}->{outputRate} Hz is not supported. Use IsSupported() to check first.",
            nameof(outputRate));
    }

    /// <summary>Returns true if a resampler can be created for the given rate pair.</summary>
    public static bool IsSupported(int inputRate, int outputRate)
    {
        foreach (var entry in Supported)
        {
            if (entry.InputRate == inputRate && entry.OutputRate == outputRate)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates the expected number of output samples for a given number of input samples.
    /// Adds 1 to account for filter boundary rounding.
    /// </summary>
    public static int CalculateOutputSize(int inputSamples, int inputRate, int outputRate)
    {
        return (int)Math.Ceiling((double)inputSamples * outputRate / inputRate) + 1;
    }
}

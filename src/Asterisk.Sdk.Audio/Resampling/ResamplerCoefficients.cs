namespace Asterisk.Sdk.Audio.Resampling;

/// <summary>
/// Pre-computed polyphase FIR filter coefficients for telephony rate conversions.
/// Uses Kaiser-windowed sinc design with 32 taps per polyphase branch.
/// Coefficients are computed once at static initialization time (AOT-safe).
/// Stored in polyphase order: [phase0_tap0..phase0_tap31, phase1_tap0..phase1_tap31, ...].
/// </summary>
internal static class ResamplerCoefficients
{
    // 8000 <-> 16000 Hz
    internal static readonly float[] Coefficients8To16k = ComputePolyphaseCoefficients(8000, 16000, L: 2, M: 1, tapsPerPhase: 32);
    internal static readonly float[] Coefficients16To8k = ComputePolyphaseCoefficients(16000, 8000, L: 1, M: 2, tapsPerPhase: 32);

    // 8000 <-> 24000 Hz
    internal static readonly float[] Coefficients8To24k = ComputePolyphaseCoefficients(8000, 24000, L: 3, M: 1, tapsPerPhase: 32);
    internal static readonly float[] Coefficients24To8k = ComputePolyphaseCoefficients(24000, 8000, L: 1, M: 3, tapsPerPhase: 32);

    // 16000 <-> 24000 Hz
    internal static readonly float[] Coefficients16To24k = ComputePolyphaseCoefficients(16000, 24000, L: 3, M: 2, tapsPerPhase: 32);
    internal static readonly float[] Coefficients24To16k = ComputePolyphaseCoefficients(24000, 16000, L: 2, M: 3, tapsPerPhase: 32);

    // 8000 <-> 48000 Hz
    internal static readonly float[] Coefficients8To48k = ComputePolyphaseCoefficients(8000, 48000, L: 6, M: 1, tapsPerPhase: 32);
    internal static readonly float[] Coefficients48To8k = ComputePolyphaseCoefficients(48000, 8000, L: 1, M: 6, tapsPerPhase: 32);

    // 16000 <-> 48000 Hz
    internal static readonly float[] Coefficients16To48k = ComputePolyphaseCoefficients(16000, 48000, L: 3, M: 1, tapsPerPhase: 32);
    internal static readonly float[] Coefficients48To16k = ComputePolyphaseCoefficients(48000, 16000, L: 1, M: 3, tapsPerPhase: 32);

    // 24000 <-> 48000 Hz
    internal static readonly float[] Coefficients24To48k = ComputePolyphaseCoefficients(24000, 48000, L: 2, M: 1, tapsPerPhase: 32);
    internal static readonly float[] Coefficients48To24k = ComputePolyphaseCoefficients(48000, 24000, L: 1, M: 2, tapsPerPhase: 32);

    /// <summary>
    /// Computes Kaiser-windowed sinc FIR filter coefficients and rearranges them
    /// into polyphase order for cache-friendly access during resampling.
    /// </summary>
    private static float[] ComputePolyphaseCoefficients(int inputRate, int outputRate, int L, int M, int tapsPerPhase)
    {
        int totalTaps = L * tapsPerPhase;

        // Normalized cutoff frequency: cutoff / (L * inputRate)
        // Cutoff is at min(inputRate, outputRate) / 2 to satisfy Nyquist on both sides
        float cutoff = (float)Math.Min(inputRate, outputRate) / (2.0f * L * inputRate);
        const float beta = 8.0f;
        float center = (totalTaps - 1) / 2.0f;

        // Compute prototype filter in natural order
        var prototype = new float[totalTaps];

        for (int n = 0; n < totalTaps; n++)
        {
            float x = n - center;

            // Sinc function: sin(2*pi*Fc*x) / (pi*x)
            float sinc = Math.Abs(x) < 1e-6f
                ? 1.0f
                : (float)(Math.Sin(2.0 * Math.PI * cutoff * x) / (Math.PI * x));

            // Kaiser window
            float window = KaiserWindow(n, totalTaps, beta);

            // Gain normalization: scale by 2*Fc*L so the passband gain is unity
            prototype[n] = sinc * window * 2.0f * cutoff * L;
        }

        // Rearrange into polyphase order:
        // polyphase[phase * tapsPerPhase + tap] = prototype[phase + tap * L]
        var polyphase = new float[totalTaps];

        for (int phase = 0; phase < L; phase++)
        {
            for (int tap = 0; tap < tapsPerPhase; tap++)
            {
                polyphase[phase * tapsPerPhase + tap] = prototype[phase + tap * L];
            }
        }

        return polyphase;
    }

    /// <summary>
    /// Kaiser window function: w(n) = I0(beta * sqrt(1 - ((2n/(N-1)) - 1)^2)) / I0(beta).
    /// </summary>
    private static float KaiserWindow(int n, int totalLength, float beta)
    {
        double x = 2.0 * n / (totalLength - 1) - 1.0;
        double arg = beta * Math.Sqrt(Math.Max(0.0, 1.0 - x * x));
        return (float)(BesselI0(arg) / BesselI0(beta));
    }

    /// <summary>
    /// Modified Bessel function of the first kind, order 0.
    /// Series expansion: I0(x) = sum_{k=0..inf} ((x/2)^2k) / (k!)^2.
    /// Converges rapidly for the values used in Kaiser window design.
    /// </summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double halfXSquared = x * x / 4.0;

        for (int k = 1; k <= 30; k++)
        {
            term *= halfXSquared / (k * k);
            sum += term;

            if (term < 1e-12 * sum)
                break;
        }

        return sum;
    }
}

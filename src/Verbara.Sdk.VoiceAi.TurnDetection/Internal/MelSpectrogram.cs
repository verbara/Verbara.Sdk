namespace Verbara.Sdk.VoiceAi.TurnDetection.Internal;

internal sealed class MelSpectrogram
{
    private const int NFft = 400;
    private const int FftSize = 512; // next power of 2
    private const int HopLength = 160;
    private const int NMels = 80;
    private const int MaxFrames = 800;

    private readonly MelFilterBank _filterBank = new();

    /// <summary>
    /// Compute log-mel spectrogram from audio samples.
    /// Output: float[80 * 800] in row-major order [mel_bin, time_frame].
    /// Pads at the BEGINNING if audio is shorter than 8s.
    /// </summary>
    public float[] Compute(ReadOnlySpan<float> audio)
    {
        // 1. Calculate number of STFT frames from the audio
        int numFrames = audio.Length >= NFft
            ? 1 + (audio.Length - NFft) / HopLength
            : (audio.Length > 0 ? 1 : 0);

        // 2. Compute STFT → power spectrum → mel energies for each frame
        var melFrames = new float[numFrames * NMels]; // [numFrames][NMels]
        Span<float> fftReal = stackalloc float[FftSize];
        Span<float> fftImag = stackalloc float[FftSize];
        Span<float> powerSpec = stackalloc float[NFft / 2 + 1];
        Span<float> melEnergies = stackalloc float[NMels];

        var hann = HannWindow.Values;

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * HopLength;

            // Zero-fill FFT buffers
            fftReal.Clear();
            fftImag.Clear();

            // Apply Hann window and copy to FFT input
            int windowLen = Math.Min(NFft, audio.Length - start);
            for (int i = 0; i < windowLen; i++)
            {
                fftReal[i] = audio[start + i] * hann[i];
            }

            // In-place radix-2 FFT
            Fft(fftReal, fftImag);

            // Power spectrum (first 201 bins)
            for (int k = 0; k < powerSpec.Length; k++)
            {
                powerSpec[k] = fftReal[k] * fftReal[k] + fftImag[k] * fftImag[k];
            }

            // Apply mel filterbank
            _filterBank.Apply(powerSpec, melEnergies);

            // Store mel energies for this frame
            for (int m = 0; m < NMels; m++)
            {
                melFrames[f * NMels + m] = melEnergies[m];
            }
        }

        // 3. Log10 scaling
        for (int i = 0; i < melFrames.Length; i++)
        {
            melFrames[i] = MathF.Log10(MathF.Max(melFrames[i], 1e-10f));
        }

        // 4. Clamp to (max - 8.0) and normalize
        float maxVal = float.MinValue;
        for (int i = 0; i < melFrames.Length; i++)
        {
            if (melFrames[i] > maxVal) maxVal = melFrames[i];
        }

        float clampMin = maxVal - 8.0f;
        for (int i = 0; i < melFrames.Length; i++)
        {
            melFrames[i] = (MathF.Max(melFrames[i], clampMin) + 4.0f) / 4.0f;
        }

        // 5. Arrange into output [80, 800] with zero-padding at BEGINNING
        var output = new float[NMels * MaxFrames];
        int frameOffset = MaxFrames - numFrames; // pad at start

        // Fill pad region with the normalized floor value for consistency
        if (numFrames < MaxFrames)
        {
            float padValue = numFrames == 0 ? 0f : (clampMin + 4.0f) / 4.0f;
            Array.Fill(output, padValue);
        }

        for (int m = 0; m < NMels; m++)
        {
            for (int f = 0; f < numFrames; f++)
            {
                output[m * MaxFrames + frameOffset + f] = melFrames[f * NMels + m];
            }
        }

        return output;
    }

    /// <summary>Radix-2 in-place Cooley-Tukey FFT.</summary>
    internal static void Fft(Span<float> real, Span<float> imag)
    {
        int n = real.Length;

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Cooley-Tukey butterfly
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2.0f * MathF.PI / len;
            float wReal = MathF.Cos(angle);
            float wImag = MathF.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                float curReal = 1.0f;
                float curImag = 0.0f;

                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j;
                    int v = i + j + len / 2;

                    float tReal = curReal * real[v] - curImag * imag[v];
                    float tImag = curReal * imag[v] + curImag * real[v];

                    real[v] = real[u] - tReal;
                    imag[v] = imag[u] - tImag;
                    real[u] += tReal;
                    imag[u] += tImag;

                    float newCurReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = newCurReal;
                }
            }
        }
    }
}

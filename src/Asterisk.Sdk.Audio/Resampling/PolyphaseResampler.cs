using System.Buffers;
using System.Runtime.InteropServices;

namespace Asterisk.Sdk.Audio.Resampling;

/// <summary>
/// Polyphase FIR resampler for telephony rate pairs.
/// Stateful (holds delay line for frame continuity). One instance per audio stream.
/// Zero heap allocation on hot path.
/// </summary>
/// <remarks>
/// Implements standard polyphase decomposition:
/// <list type="number">
///   <item>Upsample by L (conceptual — no actual zero insertion)</item>
///   <item>Apply lowpass FIR filter via polyphase branches</item>
///   <item>Downsample by M (take every M-th output)</item>
/// </list>
/// The algorithm uses a running phase accumulator to select which polyphase branch
/// to apply for each output sample. The delay line maintains state across frames
/// for continuous audio streams.
/// </remarks>
public sealed class PolyphaseResampler : IAudioTransform, IDisposable
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _l;         // upsample factor
    private readonly int _m;         // downsample factor
    private readonly float[] _coefficients; // L * tapsPerPhase values in polyphase order
    private readonly int _tapsPerPhase;
    private float[] _delayLine;      // rented from ArrayPool<float>.Shared
    private int _delayLinePos;       // circular buffer write position
    private int _phase;              // running phase accumulator (0..L-1), persists across frames
    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat InputFormat { get; }

    /// <inheritdoc/>
    public AudioFormat OutputFormat { get; }

    internal PolyphaseResampler(int inputRate, int outputRate, int l, int m, float[] coefficients)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _l = l;
        _m = m;
        _coefficients = coefficients;
        _tapsPerPhase = coefficients.Length / l;

        InputFormat = new AudioFormat(inputRate, 1, 16, AudioEncoding.LinearPcm);
        OutputFormat = new AudioFormat(outputRate, 1, 16, AudioEncoding.LinearPcm);

        // Rent delay line from pool -- size = tapsPerPhase (circular buffer for FIR history)
        _delayLine = ArrayPool<float>.Shared.Rent(_tapsPerPhase);
        _delayLine.AsSpan(0, _tapsPerPhase).Clear();
        _delayLinePos = 0;
        _phase = 0;
    }

    /// <summary>
    /// Resamples PCM16 audio. Input/output are signed 16-bit samples (as byte spans).
    /// Returns the number of bytes written to output.
    /// Output buffer must be at least <see cref="MaxOutputBytes"/> bytes.
    /// </summary>
    public int Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inputSamples = MemoryMarshal.Cast<byte, short>(input);
        var outputSamples = MemoryMarshal.Cast<byte, short>(output);

        int written = ProcessCore(inputSamples, outputSamples);
        return written * 2; // convert samples to bytes
    }

    /// <summary>
    /// Resamples PCM16 audio using short spans directly.
    /// Returns number of samples written.
    /// </summary>
    public int Process(ReadOnlySpan<short> input, Span<short> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ProcessCore(input, output);
    }

    /// <inheritdoc/>
    public int MaxOutputBytes(int inputBytes)
    {
        int inputSamples = inputBytes / 2;
        return (ResamplerFactory.CalculateOutputSize(inputSamples, _inputRate, _outputRate) + 1) * 2;
    }

    /// <summary>
    /// Core polyphase resampling algorithm.
    /// </summary>
    /// <remarks>
    /// For each input sample:
    ///   1. Store it in the circular delay line
    ///   2. While phase &lt; L, produce an output sample using sub-filter[phase], then advance phase by M
    ///   3. After the inner loop, subtract L from phase
    ///
    /// This naturally produces the correct L/M output-to-input ratio:
    /// - 8k->16k (L=2, M=1): phase goes 0->2, outputs at phase 0,1 => 2 outputs/input
    /// - 16k->8k (L=1, M=2): phase alternates, outputs every other input => 0.5 outputs/input
    /// - 16k->24k (L=3, M=2): outputs 3 samples per 2 inputs => 1.5 outputs/input
    /// </remarks>
    private int ProcessCore(ReadOnlySpan<short> input, Span<short> output)
    {
        int outputPos = 0;
        int tapsPerPhase = _tapsPerPhase;
        int l = _l;
        int m = _m;
        float[] coefficients = _coefficients;
        float[] delayLine = _delayLine;
        int delayPos = _delayLinePos;
        int phase = _phase;

        for (int i = 0; i < input.Length; i++)
        {
            // Write input sample to delay line (circular buffer)
            delayLine[delayPos] = input[i];
            delayPos++;
            if (delayPos >= tapsPerPhase)
                delayPos = 0;

            // Produce output samples for this input using polyphase branches
            while (phase < l)
            {
                // Apply FIR filter for polyphase branch 'phase'
                float sum = 0.0f;
                int coeffBase = phase * tapsPerPhase;

                for (int t = 0; t < tapsPerPhase; t++)
                {
                    int delayIdx = delayPos - 1 - t;
                    if (delayIdx < 0)
                        delayIdx += tapsPerPhase;

                    sum += delayLine[delayIdx] * coefficients[coeffBase + t];
                }

                // Clamp to short range and write output
                int sample = (int)MathF.Round(sum);
                if (sample > short.MaxValue) sample = short.MaxValue;
                else if (sample < short.MinValue) sample = short.MinValue;

                if (outputPos < output.Length)
                    output[outputPos++] = (short)sample;

                phase += m;
            }

            phase -= l;
        }

        // Persist state for next frame
        _delayLinePos = delayPos;
        _phase = phase;

        return outputPos;
    }

    /// <summary>Resets the internal delay line and phase accumulator (e.g., on stream restart).</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _delayLine.AsSpan(0, _tapsPerPhase).Clear();
        _delayLinePos = 0;
        _phase = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<float>.Shared.Return(_delayLine);
        _delayLine = [];
    }
}

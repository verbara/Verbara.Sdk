using System.Buffers.Binary;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// Generates the locally synthesized PCM waveforms committed under <c>Recordings/</c> for the
/// WebSocket TTS providers whose terms do not clear a real capture
/// (<c>docs/guides/provider-recording-protocol.md</c> §6, §7).
/// </summary>
/// <remarks>
/// <para>
/// This is the "commit a small generator" half of the source-audio rule: every <c>.raw</c> fixture in
/// this suite is the output of <see cref="Triangle"/> for three numbers recorded in its provenance
/// sidecar, so the bytes are reproducible rather than magic. Each provider's
/// <c>RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally</c> test
/// re-runs it and compares byte-for-byte.
/// </para>
/// <para>
/// Integer arithmetic only, deliberately. <c>Math.Sin</c> is not guaranteed bit-identical across
/// platforms or architectures, and these files are asserted byte-for-byte — a sine-based generator
/// would turn "the recording is intact" into "the recording is intact on x64 Linux".
/// </para>
/// <para>
/// The waveform is a signal-generator tone, not speech. No provider was called to produce it, no
/// voice — synthetic or human — is present in it, and it carries no spoken content of any kind.
/// </para>
/// </remarks>
internal static class SyntheticPcm
{
    /// <summary>
    /// Renders a symmetric triangle wave as 16-bit signed little-endian mono PCM.
    /// </summary>
    /// <param name="sampleCount">Number of samples; the result is twice this many bytes.</param>
    /// <param name="periodSamples">Samples per full cycle. Must be even and at least 2.</param>
    /// <param name="amplitude">Peak excursion, reached at the top and bottom of each cycle.</param>
    public static byte[] Triangle(int sampleCount, int periodSamples, short amplitude)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodSamples, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amplitude);

        var half = periodSamples / 2;
        var pcm = new byte[sampleCount * 2];

        for (var i = 0; i < sampleCount; i++)
        {
            var phase = i % periodSamples;

            // Rising limb then falling limb. Both numerators are non-negative, so integer division
            // truncates and floors identically — the reference implementation used to mint these
            // files used floor division, and this must agree with it exactly.
            var value = phase < half
                ? -amplitude + (2 * amplitude * phase / half)
                : amplitude - (2 * amplitude * (phase - half) / half);

            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)value);
        }

        return pcm;
    }
}

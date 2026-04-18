using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi;

namespace VoiceAiCustomProviderExample;

/// <summary>
/// Demonstrates a custom <see cref="SpeechSynthesizer"/>. Replace the body
/// with real TTS backend calls.
///
/// Overriding <see cref="ProviderName"/> with a stable literal avoids the
/// per-utterance reflection the default (<c>=> GetType().Name</c>) incurs.
/// </summary>
public sealed class MySilenceSynthesizer : SpeechSynthesizer
{
    /// <inheritdoc />
    public override string ProviderName => "MySilence";

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1 second of silence, chunked as 20ms frames (50 frames).
        const int frameDurationMs = 20;
        int bytesPerFrame = outputFormat.SampleRate / 1000 * frameDurationMs * 2; // 16-bit PCM
        var silence = new byte[bytesPerFrame];

        for (int i = 0; i < 50; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return silence;
            await Task.Delay(frameDurationMs, ct).ConfigureAwait(false);
        }
    }
}

using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Base class for text-to-speech engines. Implementations convert text into
/// a stream of audio frames in the requested format.
/// </summary>
public abstract class SpeechSynthesizer : IAsyncDisposable
{
    /// <summary>
    /// Stable, allocation-free identifier for this TTS provider (e.g. <c>"Azure"</c>, <c>"ElevenLabs"</c>).
    /// Used as an activity/metric tag in the pipeline hot path; override to avoid <c>GetType().Name</c> reflection.
    /// </summary>
    public virtual string ProviderName => GetType().Name;

    /// <summary>Synthesizes text into a stream of audio frames.</summary>
    public abstract IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        CancellationToken ct = default);

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

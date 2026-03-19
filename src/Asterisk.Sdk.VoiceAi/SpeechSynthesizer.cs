using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Base class for text-to-speech engines. Implementations convert text into
/// a stream of audio frames in the requested format.
/// </summary>
public abstract class SpeechSynthesizer : IAsyncDisposable
{
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

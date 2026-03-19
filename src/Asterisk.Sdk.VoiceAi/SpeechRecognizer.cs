using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Base class for speech-to-text engines. Implementations stream audio frames
/// and yield incremental recognition results (partial and final).
/// </summary>
public abstract class SpeechRecognizer : IAsyncDisposable
{
    /// <summary>Streams audio frames to the STT engine and yields recognition results.</summary>
    public abstract IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        CancellationToken ct = default);

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

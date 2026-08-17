using Verbara.Sdk.Audio;

namespace Verbara.Sdk.VoiceAi;

/// <summary>
/// Base class for speech-to-text engines. Implementations stream audio frames
/// and yield incremental recognition results (partial and final).
/// </summary>
public abstract class SpeechRecognizer : IAsyncDisposable
{
    /// <summary>
    /// Stable, allocation-free identifier for this STT provider (e.g. <c>"Deepgram"</c>, <c>"Whisper"</c>).
    /// Used as an activity/metric tag in the pipeline hot path; override to avoid <c>GetType().Name</c> reflection.
    /// </summary>
    public virtual string ProviderName => GetType().Name;

    /// <summary>Streams audio frames to the STT engine and yields recognition results.</summary>
    /// <remarks>
    /// <para>
    /// Every provider in this SDK signals a failure by throwing (<c>ADR-0050</c> E1). Because these
    /// are raised from the enumeration, they surface at <c>MoveNextAsync</c> — that is, from the
    /// <c>await foreach</c> — and not from the call that hands back this enumerable.
    /// </para>
    /// <para>
    /// <b>Zero transcripts is not a failure here</b>, and the asymmetry with
    /// <see cref="SpeechSynthesizer.SynthesizeAsync"/> is deliberate (<c>ADR-0050</c> E5). Voice
    /// activity detection flushes an utterance on any turn trigger, so a session carrying noise and
    /// no speech is a healthy session that yields nothing — on at least one vendor it presents as
    /// lifecycle frames with no content frames, which a blanket rule could not tell apart from a
    /// rejected session. Only a session where <em>no vendor frame arrived at all</em> is empty in the
    /// sense that throws.
    /// </para>
    /// </remarks>
    /// <exception cref="SpeechProviderFailureException">
    /// The provider reported a failure — its own error frame, a failure close code, a rejected
    /// upgrade, or a connection that died mid-stream.
    /// <see cref="SpeechProviderFailureException.Signal"/> says which.
    /// </exception>
    /// <exception cref="SpeechProviderEmptyResultException">
    /// The session ended cleanly, was not cancelled, reported no failure, and no frame of any kind
    /// arrived from the vendor.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. Cancellation is never reported as a provider failure
    /// (<c>ADR-0050</c> E6).
    /// </exception>
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

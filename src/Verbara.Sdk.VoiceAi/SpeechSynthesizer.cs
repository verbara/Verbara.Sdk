using Verbara.Sdk.Audio;

namespace Verbara.Sdk.VoiceAi;

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
    /// <remarks>
    /// <para>
    /// Every provider in this SDK signals a failure by throwing (<c>ADR-0050</c> E1); a synthesis
    /// never ends quietly having produced nothing. Because these are raised from the enumeration,
    /// they surface at <c>MoveNextAsync</c> — that is, from the <c>await foreach</c> — and not from
    /// the call that hands back this enumerable.
    /// </para>
    /// <para>
    /// A third-party implementation is free not to throw, which is why the pipeline keeps a
    /// zero-output counter as a backstop (<c>ADR-0050</c> E9). The counter is not a substitute: a
    /// caller of this method directly sees nothing unless the implementation throws.
    /// </para>
    /// <para>
    /// <paramref name="text"/> that is empty or whitespace yields no audio and does <em>not</em>
    /// throw: no provider was asked for anything, so there is no provider failure to report.
    /// </para>
    /// </remarks>
    /// <exception cref="SpeechProviderFailureException">
    /// The provider reported a failure — its own error frame, a failure close code, a rejected
    /// upgrade, or a connection that died mid-stream.
    /// <see cref="SpeechProviderFailureException.Signal"/> says which.
    /// </exception>
    /// <exception cref="SpeechProviderEmptyResultException">
    /// The session ended cleanly, was not cancelled, reported no failure, and produced no audio.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. Cancellation is never reported as a provider failure
    /// (<c>ADR-0050</c> E6), so a barge-in that ends a synthesis at zero bytes arrives here and not
    /// as a <see cref="SpeechProviderException"/>.
    /// </exception>
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

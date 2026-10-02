namespace Verbara.Sdk.VoiceAi.Internal;

/// <summary>
/// The <c>voiceai.ending</c> tag carried by <c>stt.transcriptions.cancelled</c> and
/// <c>tts.syntheses.cancelled</c>: what cut a started turn short. These four values are the whole set,
/// so the tag's cardinality is four.
/// </summary>
/// <remarks>
/// The rule the two counters implement: every recognition and every synthesis the pipeline starts is
/// counted in exactly one of <c>completed</c>, <c>failed</c> or <c>cancelled</c>, and <c>completed</c>
/// means the provider's work was used in full. A turn someone outside the provider ended is
/// <c>cancelled</c>, tagged with who. The pre-change <c>tts.syntheses.completed</c> is recoverable as
/// <c>completed</c> plus the <c>cancelled</c> increments whose ending is not
/// <see cref="SessionCancelled"/>.
/// </remarks>
internal static class TurnEnding
{
    /// <summary>The tag's key.</summary>
    public const string TagName = "voiceai.ending";

    /// <summary>
    /// The token the session runs under was cancelled: by the host that called
    /// <c>HandleSessionAsync</c>, or by the session broker's stop or disposal.
    /// </summary>
    public const string SessionCancelled = "session-cancelled";

    /// <summary>The caller spoke over the synthesis.</summary>
    public const string BargeIn = "barge-in";

    /// <summary>The pipeline was disposed while the synthesis was in flight.</summary>
    public const string Disposal = "disposal";

    /// <summary>A write found the audio session already ended: the far end left mid-playback.</summary>
    public const string FarEnd = "far-end";

    /// <summary>The single tag an increment of a <c>…cancelled</c> counter carries.</summary>
    public static KeyValuePair<string, object?> Tag(string ending) => new(TagName, ending);
}

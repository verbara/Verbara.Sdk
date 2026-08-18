namespace Verbara.Sdk.VoiceAi.Tts.Speechmatics;

/// <summary>
/// The voice identifiers the Speechmatics TTS preview accepts as the path segment of
/// <c>POST /generate/{voice}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not transcribed.</b> Every entry below was confirmed against the live endpoint on
/// 2026-08-18. Byte comparison cannot settle this question — the service is stochastic, and the
/// same voice asked twice for the same sentence returns different audio of different length — so
/// the discriminator is the speaker's median fundamental frequency, measured over three
/// syntheses of one fixed sentence. The four ids resolve to four distinct speakers separated by
/// 20-107 Hz, with a within-voice spread under 5 Hz. The path segment therefore demonstrably
/// selects the speaker.
/// </para>
/// <para>
/// <b>Why the catalog exists at all.</b> An unrecognised segment does not fail. Every value tried
/// — including deliberate nonsense — returns <c>200 audio/wav</c>, and the pitch of that audio
/// matches <see cref="Jack"/>. A misspelt voice is thus indistinguishable from success at the
/// transport layer and produces no error a caller could observe: the wrong person simply reads
/// the script. This is why <see cref="SpeechmaticsOptionsValidator"/> checks the configured value
/// at startup rather than trusting the response status.
/// </para>
/// <para>
/// <b>Case matters.</b> The comparison in <see cref="IsKnown"/> is ordinal on purpose. Measured on
/// the same date: <c>sarah</c> returns the 180 Hz speaker, while <c>Sarah</c> and <c>SARAH</c>
/// both return the ~90 Hz fallback. Case-insensitive matching here would pass validation for a
/// value the service silently ignores, which is the exact failure this type exists to prevent.
/// </para>
/// <para>
/// The service is labelled a preview by its vendor and the roster may grow. Callers who need a
/// voice this catalog does not list yet can set
/// <see cref="SpeechmaticsOptions.AllowUnlistedVoice"/> and take the check off.
/// </para>
/// </remarks>
public static class SpeechmaticsVoices
{
    /// <summary>Sarah — English, female, UK. Measured median F0 ≈ 180 Hz.</summary>
    public const string Sarah = "sarah";

    /// <summary>Theo — English, male, UK. Measured median F0 ≈ 109 Hz.</summary>
    public const string Theo = "theo";

    /// <summary>Megan — English, female, US. Measured median F0 ≈ 195 Hz.</summary>
    public const string Megan = "megan";

    /// <summary>
    /// Jack — English, male, US. Measured median F0 ≈ 88 Hz. Default for
    /// <see cref="SpeechmaticsOptions.Voice"/>, and also the voice the service falls back to for
    /// any segment it does not recognise.
    /// </summary>
    public const string Jack = "jack";

    /// <summary>Every known voice id, in the order the vendor's quickstart lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Sarah, Theo, Megan, Jack];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="voice"/> is one of <see cref="All"/>,
    /// compared ordinally. See the type remarks for why the comparison is case-sensitive.
    /// </summary>
    public static bool IsKnown(string? voice)
    {
        if (string.IsNullOrEmpty(voice))
        {
            return false;
        }

        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], voice, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

namespace Verbara.Sdk.Ari.Audio;

/// <summary>What an audio format name says about the audio it carries.</summary>
internal static class AudioFormats
{
    /// <summary>
    /// The sample rate in Hz of <paramref name="format"/>, as Asterisk names its formats; 8000 for a name it does not
    /// list. The one derivation behind every stream's <see cref="IAudioStream.SampleRate"/> and
    /// <see cref="ChanWebSocketMediaStart.Rate"/>, so the two cannot disagree.
    /// </summary>
    public static int SampleRateOf(string format) => format.ToLowerInvariant() switch
    {
        "slin16" or "slin/16000" => 16000,
        "slin" or "slin/8000" or "slin8" => 8000,
        "slin32" or "slin/32000" => 32000,
        "slin48" or "slin/48000" => 48000,
        "ulaw" or "alaw" or "g711" => 8000,
        "g722" => 16000,
        "opus" => 48000,
        _ => 8000
    };
}

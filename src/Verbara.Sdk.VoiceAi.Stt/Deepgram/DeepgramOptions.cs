using System.ComponentModel.DataAnnotations;

namespace Verbara.Sdk.VoiceAi.Stt.Deepgram;

/// <summary>Configuration options for the Deepgram WebSocket STT provider.</summary>
public sealed class DeepgramOptions
{
    /// <summary>Deepgram API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket endpoint to open, including the route. Defaults to Deepgram's hosted realtime
    /// endpoint; point it elsewhere for a self-hosted deployment.
    /// </summary>
    /// <remarks>
    /// The client used to hard-code this host, which left tests no way in except a constructor that
    /// replaced the whole URL and skipped the credential — so the route the client really asks for
    /// was exercised by nothing. Its sibling <c>DeepgramTtsOptions.BaseUri</c> has always carried the
    /// route this way.
    /// </remarks>
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://api.deepgram.com/v1/listen";

    /// <summary>Deepgram model name (default: nova-2).</summary>
    public string Model { get; set; } = "nova-2";

    /// <summary>Language code for recognition.</summary>
    public string Language { get; set; } = "es";

    /// <summary>Whether to receive interim (partial) results.</summary>
    public bool InterimResults { get; set; } = true;

    /// <summary>Whether to enable punctuation in transcripts.</summary>
    public bool Punctuate { get; set; } = true;
}

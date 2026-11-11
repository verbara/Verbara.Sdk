using System.Text.Json;

namespace Verbara.Sdk.VoiceAi.Stt.Internal;

/// <summary>
/// Reads what a recognizer's envelope peek needs from a vendor message its typed decode could not read.
/// The failure test itself stays in each recognizer, because it is that vendor's own (the kind lives in
/// <c>type</c> on one vendor and in <c>message</c> on another); so does the throw.
/// </summary>
internal static class SttErrorEnvelope
{
    /// <summary>
    /// The error code at <paramref name="member"/> of <paramref name="root"/> — the member the recognizer's
    /// readable branch reads its code from — when it is a string or a number; otherwise
    /// <see langword="null"/>. A number is kept as the vendor wrote it.
    /// </summary>
    internal static string? Code(JsonElement root, string member)
    {
        if (!root.TryGetProperty(member, out var code)) return null;

        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null,
        };
    }
}

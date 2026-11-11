using System.Text.Json;

namespace Verbara.Sdk.VoiceAi.Tts.Internal;

/// <summary>
/// Reads what a synthesizer's envelope peek needs from a vendor message its typed decode could not read.
/// The failure test itself stays in each synthesizer, because it is that vendor's own (a <c>type</c>
/// discriminator on one vendor, a bare <c>error</c> member on another); so does the throw.
/// </summary>
internal static class TtsErrorEnvelope
{
    /// <summary>
    /// The error code at <paramref name="member"/> of <paramref name="root"/> — the member the synthesizer's
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

    /// <summary>
    /// Whether <paramref name="root"/> carries an <c>error</c> member that is present, not
    /// <see langword="null"/> and not an empty string — the bare-member failure test of a vendor whose
    /// failure frame has no discriminator. A member of any other kind (an object, a number) still names a
    /// failure: it is the member the vendor marks a failure with, sent in a shape this client cannot read.
    /// </summary>
    internal static bool HasErrorMember(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => error.GetString() is { Length: > 0 },
            _ => true,
        };
}

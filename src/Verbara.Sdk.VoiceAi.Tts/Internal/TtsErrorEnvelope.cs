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
    /// Whether <paramref name="root"/> carries an <c>error</c> member that holds a value — the bare-member
    /// failure test of a vendor whose failure frame has no discriminator. A value names a failure in any
    /// shape (a string, an object, an array, a number, <see langword="true"/>): it is the member the vendor
    /// marks a failure with, sent in a shape this client cannot read. The empty value of each kind —
    /// <see langword="null"/>, <c>""</c>, <see langword="false"/>, <c>0</c>, <c>{}</c>, <c>[]</c> — is how a
    /// JSON reply says "no error", and names none.
    /// </summary>
    /// <remarks>
    /// Neither vendor publishes a contract for this member; the only shape measured is a non-empty string.
    /// The line falls on the side of the cheaper mistake: an empty value read as a failure ends a healthy
    /// synthesis, while a "no error" value read as none costs one message, counted on
    /// <c>tts.frames.unparseable</c> and skipped.
    /// </remarks>
    internal static bool HasErrorMember(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => error.GetString() is { Length: > 0 },
            JsonValueKind.Number => !error.TryGetDouble(out var number) || number != 0,
            JsonValueKind.Object => error.EnumerateObject().MoveNext(),
            JsonValueKind.Array => error.GetArrayLength() > 0,
            _ => true,
        };
}

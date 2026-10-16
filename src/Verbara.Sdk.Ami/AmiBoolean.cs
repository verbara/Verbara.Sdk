using System.Text;

namespace Verbara.Sdk.Ami;

/// <summary>
/// Reads an AMI header value as a boolean, with the true/false vocabulary Asterisk itself uses.
/// </summary>
/// <remarks>
/// <para>
/// <c>yes</c>, <c>true</c>, <c>y</c>, <c>t</c>, <c>1</c> and <c>on</c> read <see langword="true"/>;
/// <c>no</c>, <c>false</c>, <c>n</c>, <c>f</c>, <c>0</c> and <c>off</c> read <see langword="false"/>.
/// These are the words Asterisk's own <c>ast_true()</c> and <c>ast_false()</c> accept. In addition,
/// <c>enabled</c> reads <see langword="true"/> and <c>disabled</c> reads <see langword="false"/>, which is
/// how chan_dahdi renders its DND flag. Matching is ordinal and ignores ASCII case.
/// </para>
/// <para>
/// Any other value, an empty value and <see langword="null"/> (an absent header) read
/// <see langword="null"/>: unknown, never <see langword="false"/>. The value is not trimmed; the AMI
/// protocol reader already trims header values.
/// </para>
/// <para>
/// Every <see cref="Nullable{T}"/> <see cref="bool"/> member of a generated AMI event or response is read
/// with this method. Use it to read the yes/no members the SDK keeps as text (for example the
/// <c>Muted</c> and <c>Talking</c> members of the ConfBridge list events) the same way.
/// </para>
/// </remarks>
public static class AmiBoolean
{
    /// <summary>
    /// Parses an AMI header value with Asterisk's true/false vocabulary.
    /// </summary>
    /// <param name="value">The header value as received, or <see langword="null"/> when the header is absent.</param>
    /// <returns>
    /// <see langword="true"/> or <see langword="false"/> for a value in the vocabulary; otherwise
    /// <see langword="null"/>, including for an empty or <see langword="null"/> value.
    /// </returns>
    public static bool? Parse(string? value)
    {
        if (value is null)
            return null;

        ReadOnlySpan<char> v = value;
        return v.Length switch
        {
            1 => v[0] switch
            {
                'y' or 'Y' or 't' or 'T' or '1' => true,
                'n' or 'N' or 'f' or 'F' or '0' => false,
                _ => null,
            },
            2 when Ascii.EqualsIgnoreCase(v, "on") => true,
            2 when Ascii.EqualsIgnoreCase(v, "no") => false,
            3 when Ascii.EqualsIgnoreCase(v, "yes") => true,
            3 when Ascii.EqualsIgnoreCase(v, "off") => false,
            4 when Ascii.EqualsIgnoreCase(v, "true") => true,
            5 when Ascii.EqualsIgnoreCase(v, "false") => false,
            7 when Ascii.EqualsIgnoreCase(v, "enabled") => true,
            8 when Ascii.EqualsIgnoreCase(v, "disabled") => false,
            _ => null,
        };
    }
}

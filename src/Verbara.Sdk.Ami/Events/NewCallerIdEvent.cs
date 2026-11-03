using System.Globalization;
using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

/// <summary>A channel's caller identity changed (<c>NewCallerid</c>, class <c>call</c>).</summary>
[VerbaraMapping("NewCallerId")]
public sealed class NewCallerIdEvent : ChannelEventBase
{
    private int? _cidCallingPres;

    /// <summary>
    /// The calling presentation as a number: the value assigned to this member, or else the number
    /// <see cref="CidCallingPresTxt"/> starts with, or <see langword="null"/> when it starts with none. Reading it never
    /// fails, whatever the header holds.
    /// </summary>
    public int? CidCallingPres
    {
        get => _cidCallingPres ?? LeadingNumber(CidCallingPresTxt);
        set => _cidCallingPres = value;
    }

    /// <summary>
    /// The <c>CID-CallingPres</c> header, unchanged: the number followed by its description, for example
    /// <c>0 (Presentation Allowed, Not Screened)</c>.
    /// </summary>
    [VerbaraMapping("CID-CallingPres")]
    public string? CidCallingPresTxt { get; set; }

    public string? LinkedId { get; set; }

    private static int? LeadingNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;

        return digits > 0 && int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

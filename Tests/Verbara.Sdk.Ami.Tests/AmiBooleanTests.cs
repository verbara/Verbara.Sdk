using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests;

/// <summary>
/// <see cref="AmiBoolean.Parse"/> reads Asterisk's own true/false vocabulary (the words <c>ast_true()</c> and
/// <c>ast_false()</c> accept, plus chan_dahdi's <c>enabled</c>/<c>disabled</c>), ignoring case, and reads
/// anything else as unknown.
/// </summary>
public sealed class AmiBooleanTests
{
    public static TheoryData<string> TrueTokens() => Casings("yes", "true", "y", "t", "1", "on", "enabled");

    public static TheoryData<string> FalseTokens() => Casings("no", "false", "n", "f", "0", "off", "disabled");

    [Theory]
    [MemberData(nameof(TrueTokens))]
    public void Parse_ShouldReturnTrue_WhenValueIsATrueToken(string value)
    {
        AmiBoolean.Parse(value).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(FalseTokens))]
    public void Parse_ShouldReturnFalse_WhenValueIsAFalseToken(string value)
    {
        AmiBoolean.Parse(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData(" yes")]
    [InlineData("yes ")]
    [InlineData("ye")]
    [InlineData("yess")]
    [InlineData("enable")]
    [InlineData("o")]
    [InlineData("x")]
    [InlineData("ı")]
    public void Parse_ShouldReturnNull_WhenValueIsOutsideTheVocabulary(string value)
    {
        AmiBoolean.Parse(value).Should().BeNull();
    }

    [Fact]
    public void Parse_ShouldReturnNull_WhenValueIsNull()
    {
        AmiBoolean.Parse(null).Should().BeNull();
    }

    [Fact]
    public void Parse_ShouldIgnoreOnlyAsciiCase_WhenValueHasANonAsciiLookalike()
    {
        // U+0130 (capital I with dot above) and U+0131 (dotless i) fold to 'i' under some culture rules; the
        // vocabulary is ASCII, so neither spells "disabled" or "enabled".
        AmiBoolean.Parse("DİSABLED").Should().BeNull();
        AmiBoolean.Parse("dısabled").Should().BeNull();
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    public void Parse_ShouldReadAYesNoTextMember_WhenConfbridgeListRowCarriesIt(string value, bool expected)
    {
        // ConfbridgeList rows carry Muted and Talking as text "Yes"/"No" (Asterisk 20.20.1, 22.10.1, 23.4.1);
        // a consumer reads them with the same rule as a bool? member.
        AmiBoolean.Parse(value).Should().Be(expected);
    }

    // Each token is written lower case; adds it in upper case and with its first letter upper case.
    private static TheoryData<string> Casings(params string[] tokens)
    {
        var forms = tokens
            .SelectMany(static token => new[] { token, token.ToUpperInvariant(), char.ToUpperInvariant(token[0]) + token[1..] })
            .Distinct(StringComparer.Ordinal);
        var data = new TheoryData<string>();
        foreach (var form in forms)
            data.Add(form);

        return data;
    }
}

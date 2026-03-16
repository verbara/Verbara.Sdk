using Asterisk.Sdk.Config;
using FluentAssertions;

namespace Asterisk.Sdk.Config.Tests;

public class ConfigQuotedSemicolonTests
{
    [Fact]
    public void Parse_ShouldPreserveSemicolonsInsideQuotes()
    {
        var input = """
            [general]
            password = "pass;word"
            uri = "sip:user@host;transport=tcp"
            normal = value ; this is a comment
            """;

        var result = ConfigFileReader.Parse(new StringReader(input));
        var section = result.GetCategory("general");

        section.Should().NotBeNull();
        section!.Variables["password"].Should().Be("\"pass;word\"");
        section.Variables["uri"].Should().Be("\"sip:user@host;transport=tcp\"");
        section.Variables["normal"].Should().Be("value");
    }

    [Fact]
    public void Parse_ShouldStripComment_WhenNoQuotes()
    {
        var input = """
            [test]
            key = val ; comment
            """;

        var result = ConfigFileReader.Parse(new StringReader(input));
        var section = result.GetCategory("test");

        section.Should().NotBeNull();
        section!.Variables["key"].Should().Be("val");
    }
}

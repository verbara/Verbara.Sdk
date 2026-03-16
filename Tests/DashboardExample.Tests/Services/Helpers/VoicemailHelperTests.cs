using DashboardExample.Services.Helpers;
using FluentAssertions;

namespace DashboardExample.Tests.Services.Helpers;

public class VoicemailHelperTests
{
    [Fact]
    public void FormatVoicemailValue_ShouldProduceCorrectFormat()
    {
        var result = VoicemailHelper.FormatVoicemailValue("1234", "John Doe", "john@example.com", 50);

        result.Should().Be("1234,John Doe,john@example.com,,attach=yes|maxmsg=50");
    }

    [Fact]
    public void FormatVoicemailValue_ShouldHandleNullEmail()
    {
        var result = VoicemailHelper.FormatVoicemailValue("9999", "Jane Smith", null, 25);

        result.Should().Be("9999,Jane Smith,,,attach=yes|maxmsg=25");
    }

    [Fact]
    public void ParseVoicemailValue_ShouldExtractFields()
    {
        var result = VoicemailHelper.ParseVoicemailValue("1234,John Doe,john@example.com,,attach=yes|maxmsg=50");

        result.Should().NotBeNull();
        result!.Pin.Should().Be("1234");
        result.FullName.Should().Be("John Doe");
        result.Email.Should().Be("john@example.com");
        result.MaxMessages.Should().Be(50);
    }

    [Fact]
    public void ParseVoicemailValue_ShouldHandleMinimalFormat()
    {
        var result = VoicemailHelper.ParseVoicemailValue("0000,Bob");

        result.Should().NotBeNull();
        result!.Pin.Should().Be("0000");
        result.FullName.Should().Be("Bob");
        result.Email.Should().BeNullOrEmpty();
        result.MaxMessages.Should().Be(50);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseVoicemailValue_ShouldReturnNull_WhenEmpty(string? value)
    {
        var result = VoicemailHelper.ParseVoicemailValue(value);

        result.Should().BeNull();
    }
}

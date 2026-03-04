using Asterisk.Sdk.Ami.Responses;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Responses;

public class CommandResponseTests
{
    [Fact]
    public void Output_ShouldExtractCommandOutput()
    {
        var response = new CommandResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["__CommandOutput"] = "Module                         Description\nchan_sip.so                    SIP Channel"
            }
        };

        response.Output.Should().Be("Module                         Description\nchan_sip.so                    SIP Channel");
    }

    [Fact]
    public void Output_ShouldBeNull_WhenNoCommandOutputField()
    {
        var response = new CommandResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Response"] = "Success"
            }
        };

        response.Output.Should().BeNull();
    }

    [Fact]
    public void Output_ShouldBeNull_WhenRawFieldsNull()
    {
        var response = new CommandResponse { RawFields = null };
        response.Output.Should().BeNull();
    }
}

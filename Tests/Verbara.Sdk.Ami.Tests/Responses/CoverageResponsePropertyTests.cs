using Verbara.Sdk.Ami.Responses;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Responses;

public sealed class CoverageResponsePropertyTests
{
    [Fact]
    public void MailboxCountResponse_ShouldHaveAllProperties()
    {
        var response = new MailboxCountResponse
        {
            Mailbox = "100@default",
            UrgMessages = 1,
            NewMessages = 3,
            OldMessages = 10
        };
        response.Mailbox.Should().Be("100@default");
        response.UrgMessages.Should().Be(1);
        response.NewMessages.Should().Be(3);
        response.OldMessages.Should().Be(10);
    }

    [Fact]
    public void MailboxStatusResponse_ShouldHaveProperties()
    {
        var response = new MailboxStatusResponse { Mailbox = "200@default", Waiting = true };
        response.Mailbox.Should().Be("200@default");
        response.Waiting.Should().BeTrue();
    }

    [Fact]
    public void ExtensionStateResponse_ShouldHaveProperties()
    {
        var response = new ExtensionStateResponse
        {
            Exten = "100",
            Context = "default",
            Hint = "SIP/100",
            Status = 0,
            StatusText = "Idle"
        };
        response.Exten.Should().Be("100");
        response.Context.Should().Be("default");
        response.Hint.Should().Be("SIP/100");
        response.Status.Should().Be(0);
        response.StatusText.Should().Be("Idle");
    }

    [Fact]
    public void FaxLicenseStatusResponse_ShouldHaveProperties()
    {
        var response = new FaxLicenseStatusResponse { PortsLicensed = 4 };
        response.PortsLicensed.Should().Be(4);
    }

}

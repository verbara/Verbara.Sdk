using Asterisk.Sdk.Activities.Models;
using FluentAssertions;

namespace Asterisk.Sdk.Activities.Tests.Models;

public class CallTests
{
    [Fact]
    public void NewCall_ShouldHaveCorrectDefaults()
    {
        var call = new Call { Direction = CallDirection.Outbound };

        call.State.Should().Be(CallState.New);
        call.Direction.Should().Be(CallDirection.Outbound);
        call.Channels.Should().BeEmpty();
        call.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AddChannel_ShouldTrackChannels()
    {
        var call = new Call { Direction = CallDirection.Outbound };
        var ch = new PbxChannel { Name = "PJSIP/2000-001", UniqueId = "123.1" };

        call.AddChannel(ch);

        call.Channels.Should().HaveCount(1);
        call.Channels[0].Name.Should().Be("PJSIP/2000-001");
    }

    [Fact]
    public void RemoveChannel_ShouldRemoveByUniqueId()
    {
        var call = new Call { Direction = CallDirection.Inbound };
        call.AddChannel(new PbxChannel { Name = "SIP/100", UniqueId = "1.1" });
        call.AddChannel(new PbxChannel { Name = "SIP/200", UniqueId = "1.2" });

        call.RemoveChannel("1.1");

        call.Channels.Should().HaveCount(1);
        call.Channels[0].UniqueId.Should().Be("1.2");
    }

    [Fact]
    public void EndPoint_Parse_ShouldExtractTechAndResource()
    {
        var ep = EndPoint.Parse("PJSIP/2000-00000001");

        ep.Should().NotBeNull();
        ep!.Tech.Should().Be(TechType.PJSIP);
        ep.Resource.Should().Be("2000");
    }

    [Fact]
    public void EndPoint_Parse_ShouldHandleSimpleFormat()
    {
        var ep = EndPoint.Parse("SIP/3000");

        ep.Should().NotBeNull();
        ep!.Tech.Should().Be(TechType.SIP);
        ep.Resource.Should().Be("3000");
    }

    [Fact]
    public void EndPoint_ToString_ShouldFormatCorrectly()
    {
        var ep = new EndPoint(TechType.PJSIP, "agent01");
        ep.ToString().Should().Be("PJSIP/agent01");
    }

    [Fact]
    public void PhoneNumber_ToString_WithName()
    {
        var pn = new PhoneNumber("5551234", "John Doe");
        pn.ToString().Should().Be("\"John Doe\" <5551234>");
    }

    [Fact]
    public void PhoneNumber_ToString_WithoutName()
    {
        var pn = new PhoneNumber("5551234");
        pn.ToString().Should().Be("5551234");
    }
}

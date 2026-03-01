using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Responses;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Responses;

public class ResponsePropertyTests
{
    [Fact]
    public void PingResponse_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(PingResponse).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Ping");
    }

    [Fact]
    public void PingResponse_ShouldInheritFromManagerResponse()
    {
        typeof(PingResponse).Should().BeAssignableTo<ManagerResponse>();
    }

    [Fact]
    public void PingResponse_ShouldHaveProperties()
    {
        var resp = new PingResponse { Ping = "Pong", Timestamp = "1234567890.000" };
        resp.Ping.Should().Be("Pong");
        resp.Timestamp.Should().Be("1234567890.000");
    }

    [Fact]
    public void CoreSettingsResponse_ShouldHaveExpectedProperties()
    {
        var resp = new CoreSettingsResponse
        {
            AmiVersion = "6.0.0",
            AsteriskVersion = "20.0.0",
            SystemName = "asterisk",
            CoreMaxCalls = 0,
            CoreMaxLoadAvg = 0.0,
            CoreRunUser = "asterisk",
            CoreRunGroup = "asterisk"
        };

        resp.AmiVersion.Should().Be("6.0.0");
        resp.AsteriskVersion.Should().Be("20.0.0");
        resp.SystemName.Should().Be("asterisk");
        resp.CoreRunUser.Should().Be("asterisk");
    }

    [Fact]
    public void CoreStatusResponse_ShouldHaveExpectedProperties()
    {
        var resp = new CoreStatusResponse
        {
            CoreStartupDate = "2024-01-01",
            CoreStartupTime = "12:00:00",
            CoreReloadDate = "2024-01-01",
            CoreReloadTime = "12:00:00",
            CoreCurrentCalls = 5
        };

        resp.CoreStartupDate.Should().Be("2024-01-01");
        resp.CoreCurrentCalls.Should().Be(5);
    }

    [Fact]
    public void ManagerResponse_BaseProperties_ShouldWork()
    {
        var resp = new ManagerResponse
        {
            ActionId = "test-1",
            Response = "Success",
            Message = "Pong"
        };

        resp.ActionId.Should().Be("test-1");
        resp.Response.Should().Be("Success");
        resp.Message.Should().Be("Pong");
    }

    [Fact]
    public void ManagerResponse_RawFields_ShouldBeSettable()
    {
        var fields = new Dictionary<string, string> { ["Key1"] = "Value1" };
        var resp = new ManagerResponse { RawFields = fields };
        resp.RawFields.Should().ContainKey("Key1");
    }
}

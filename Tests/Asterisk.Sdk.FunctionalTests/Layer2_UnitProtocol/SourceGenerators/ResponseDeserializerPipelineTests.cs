namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using Asterisk.Sdk.Ami.Responses;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class ResponseDeserializerPipelineTests
{
    /// <summary>
    /// Helper: builds an AmiMessage from a dictionary with a Response key.
    /// </summary>
    private static AmiMessage CreateResponseMessage(Dictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Response"] = "Success",
        };

        if (extra is not null)
        {
            foreach (var (k, v) in extra)
                fields[k] = v;
        }

        return new AmiMessage(fields);
    }

    [Fact]
    public void Deserialize_ShouldMapBaseResponseFields()
    {
        var msg = CreateResponseMessage(new Dictionary<string, string>
        {
            ["ActionID"] = "abc-123",
            ["Message"] = "Pong",
        });

        var response = GeneratedResponseDeserializer.Deserialize(msg, "Ping");

        response.Should().BeOfType<PingResponse>();
        response.Response.Should().Be("Success");
        response.Message.Should().Be("Pong");
        response.ActionId.Should().Be("abc-123");
    }

    [Fact]
    public void Deserialize_CommandResponse_ShouldExtractOutput()
    {
        var msg = CreateResponseMessage(new Dictionary<string, string>
        {
            ["ActionID"] = "cmd-001",
            ["__CommandOutput"] = "SIP/peer-0001  192.168.1.10  OK (15 ms)",
        });

        var response = GeneratedResponseDeserializer.Deserialize(msg, "Command");

        response.Should().BeOfType<CommandResponse>();
        var typed = (CommandResponse)response;
        typed.Output.Should().Be("SIP/peer-0001  192.168.1.10  OK (15 ms)");
    }

    [Fact]
    public void Deserialize_TypedResponse_ShouldParseNumericFields()
    {
        var msg = CreateResponseMessage(new Dictionary<string, string>
        {
            ["ActionID"] = "cs-001",
            ["AmiVersion"] = "6.0.0",
            ["AsteriskVersion"] = "20.4.0",
            ["SystemName"] = "pbx01",
            ["CoreMaxCalls"] = "500",
            ["CoreMaxLoadAvg"] = "0.95",
            ["CoreRunUser"] = "asterisk",
            ["CoreRunGroup"] = "asterisk",
            ["CoreMaxFilehandles"] = "32768",
        });

        var response = GeneratedResponseDeserializer.Deserialize(msg, "CoreSettings");

        response.Should().BeOfType<CoreSettingsResponse>();
        var typed = (CoreSettingsResponse)response;
        typed.AmiVersion.Should().Be("6.0.0");
        typed.AsteriskVersion.Should().Be("20.4.0");
        typed.SystemName.Should().Be("pbx01");
        typed.CoreMaxCalls.Should().Be(500);
        typed.CoreMaxLoadAvg.Should().BeApproximately(0.95, 0.001);
        typed.CoreRunUser.Should().Be("asterisk");
        typed.CoreRunGroup.Should().Be("asterisk");
        typed.CoreMaxFilehandles.Should().Be(32768);
    }

    [Fact]
    public void Deserialize_UnknownAction_ShouldReturnBaseManagerResponse()
    {
        var msg = CreateResponseMessage(new Dictionary<string, string>
        {
            ["ActionID"] = "unknown-001",
            ["Message"] = "Some response",
        });

        var response = GeneratedResponseDeserializer.Deserialize(msg, "NonExistentAction");

        response.Should().BeOfType<ManagerResponse>();
        response.Response.Should().Be("Success");
        response.ActionId.Should().Be("unknown-001");
        response.Message.Should().Be("Some response");
    }
}

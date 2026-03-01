using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests;

public class AriModelTests
{
    [Fact]
    public void AriChannel_ShouldDeserializeFromJson()
    {
        var json = """{"id":"ch-001","name":"PJSIP/2000-00000001","state":"Up"}""";

        var channel = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel);

        channel.Should().NotBeNull();
        channel!.Id.Should().Be("ch-001");
        channel.Name.Should().Be("PJSIP/2000-00000001");
        channel.State.Should().Be(AriChannelState.Up);
    }

    [Fact]
    public void AriBridge_ShouldDeserializeFromJson()
    {
        var json = """{"id":"br-001","technology":"simple_bridge","bridge_type":"mixing","channels":["ch-001","ch-002"]}""";

        var bridge = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriBridge);

        bridge.Should().NotBeNull();
        bridge!.Id.Should().Be("br-001");
        bridge.BridgeType.Should().Be("mixing");
        bridge.Channels.Should().HaveCount(2);
    }

    [Fact]
    public void AriChannel_ShouldSerializeToJson()
    {
        var channel = new AriChannel { Id = "test-1", Name = "SIP/100", State = AriChannelState.Ring };

        var json = JsonSerializer.Serialize(channel, AriJsonContext.Default.AriChannel);

        json.Should().Contain("\"id\":\"test-1\"");
        json.Should().Contain("\"name\":\"SIP/100\"");
    }

    [Fact]
    public void AriEvent_ShouldParseBasicEvent()
    {
        var json = """{"type":"StasisStart","application":"myapp","timestamp":"2026-03-01T12:00:00Z"}""";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var evt = new AriEvent
        {
            Type = root.GetProperty("type").GetString(),
            Application = root.GetProperty("application").GetString(),
            RawJson = json
        };

        evt.Type.Should().Be("StasisStart");
        evt.Application.Should().Be("myapp");
        evt.RawJson.Should().Contain("StasisStart");
    }

    [Fact]
    public void AriChannel_ShouldHandleSnakeCaseProperties()
    {
        var json = """{"id":"ch-1","name":"SIP/100","state":"Up","bridge_type":"mixing"}""";

        var channel = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannel);

        channel.Should().NotBeNull();
        channel!.Id.Should().Be("ch-1");
    }
}

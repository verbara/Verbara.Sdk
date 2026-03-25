using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Events;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public sealed class AriEventDeserializationTests
{
    [Fact]
    public void BridgeDestroyedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "BridgeDestroyed",
            "bridge": {
                "id": "br-1",
                "bridge_type": "mixing",
                "technology": "simple_bridge"
            }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.BridgeDestroyedEvent);

        evt.Should().NotBeNull();
        evt!.Bridge.Should().NotBeNull();
        evt.Bridge!.Id.Should().Be("br-1");
        evt.Bridge.BridgeType.Should().Be("mixing");
    }

    [Fact]
    public void ChannelEnteredBridgeEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "ChannelEnteredBridge",
            "bridge": { "id": "br-1" },
            "channel": { "id": "ch-1", "name": "PJSIP/2000-001", "state": "Up" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelEnteredBridgeEvent);

        evt.Should().NotBeNull();
        evt!.Bridge.Should().NotBeNull();
        evt.Bridge!.Id.Should().Be("br-1");
        evt.Channel.Should().NotBeNull();
        evt.Channel!.Id.Should().Be("ch-1");
    }

    [Fact]
    public void ChannelLeftBridgeEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "ChannelLeftBridge",
            "bridge": { "id": "br-1" },
            "channel": { "id": "ch-1", "name": "PJSIP/2000-001" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelLeftBridgeEvent);

        evt.Should().NotBeNull();
        evt!.Bridge!.Id.Should().Be("br-1");
        evt.Channel!.Id.Should().Be("ch-1");
    }

    [Fact]
    public void PlaybackStartedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "PlaybackStarted",
            "playback": { "id": "pb-1", "media_uri": "sound:hello-world", "state": "playing" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.PlaybackStartedEvent);

        evt.Should().NotBeNull();
        evt!.Playback.Should().NotBeNull();
        evt.Playback!.Id.Should().Be("pb-1");
        evt.Playback.MediaUri.Should().Be("sound:hello-world");
        evt.Playback.State.Should().Be("playing");
    }

    [Fact]
    public void PlaybackFinishedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "PlaybackFinished",
            "playback": { "id": "pb-1", "media_uri": "sound:goodbye", "state": "done" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.PlaybackFinishedEvent);

        evt.Should().NotBeNull();
        evt!.Playback!.Id.Should().Be("pb-1");
        evt.Playback.State.Should().Be("done");
    }

    [Fact]
    public void ChannelTalkingStartedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "ChannelTalkingStarted",
            "channel": { "id": "ch-1", "name": "PJSIP/2000-001", "state": "Up" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelTalkingStartedEvent);

        evt.Should().NotBeNull();
        evt!.Channel.Should().NotBeNull();
        evt.Channel!.Id.Should().Be("ch-1");
    }

    [Fact]
    public void ChannelConnectedLineEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "ChannelConnectedLine",
            "channel": { "id": "ch-1", "name": "PJSIP/2000-001" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelConnectedLineEvent);

        evt.Should().NotBeNull();
        evt!.Channel!.Id.Should().Be("ch-1");
    }

    [Fact]
    public void RecordingFinishedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "RecordingFinished",
            "recording": { "name": "rec-1", "format": "wav", "state": "done" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.RecordingFinishedEvent);

        evt.Should().NotBeNull();
        evt!.Recording.Should().NotBeNull();
        evt.Recording!.Name.Should().Be("rec-1");
        evt.Recording.Format.Should().Be("wav");
        evt.Recording.State.Should().Be("done");
    }

    [Fact]
    public void ChannelToneDetectedEvent_ShouldDeserialize()
    {
        const string json = """
        {
            "type": "ChannelToneDetected",
            "channel": { "id": "ch-tone", "name": "PJSIP/3000-001" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelToneDetectedEvent);

        evt.Should().NotBeNull();
        evt!.Channel!.Id.Should().Be("ch-tone");
    }

    [Fact]
    public void BridgeAttendedTransferEvent_ShouldDeserialize_WithMinimalFields()
    {
        const string json = """
        {
            "type": "BridgeAttendedTransfer",
            "result": "Success",
            "transferer_first_leg": { "id": "ch-1", "name": "PJSIP/100" },
            "transferer_second_leg": { "id": "ch-2", "name": "PJSIP/200" },
            "is_external": false,
            "destination_type": "bridge"
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.BridgeAttendedTransferEvent);

        evt.Should().NotBeNull();
        evt!.Result.Should().Be("Success");
        evt.TransfererFirstLeg!.Id.Should().Be("ch-1");
        evt.TransfererSecondLeg!.Id.Should().Be("ch-2");
        evt.IsExternal.Should().BeFalse();
        evt.DestinationType.Should().Be("bridge");
    }

    [Fact]
    public void BridgeAttendedTransferEvent_ShouldDeserialize_WithFullFields()
    {
        const string json = """
        {
            "type": "BridgeAttendedTransfer",
            "result": "Success",
            "transferer_first_leg": { "id": "ch-1" },
            "transferer_first_leg_bridge": { "id": "br-1" },
            "transferer_second_leg": { "id": "ch-2" },
            "transferer_second_leg_bridge": { "id": "br-2" },
            "transferee": { "id": "ch-3" },
            "transfer_target": { "id": "ch-4" },
            "destination_type": "threeway",
            "destination_bridge": "br-dest",
            "destination_application": "myapp",
            "destination_link_first_leg": { "id": "ch-5" },
            "destination_link_second_leg": { "id": "ch-6" },
            "destination_threeway_channel": { "id": "ch-7" },
            "destination_threeway_bridge": { "id": "br-3" },
            "is_external": true,
            "replace_channel": { "id": "ch-8" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.BridgeAttendedTransferEvent);

        evt.Should().NotBeNull();
        evt!.TransfererFirstLegBridge!.Id.Should().Be("br-1");
        evt.TransfererSecondLegBridge!.Id.Should().Be("br-2");
        evt.Transferee!.Id.Should().Be("ch-3");
        evt.TransferTarget!.Id.Should().Be("ch-4");
        evt.DestinationType.Should().Be("threeway");
        evt.DestinationBridge.Should().Be("br-dest");
        evt.DestinationApplication.Should().Be("myapp");
        evt.DestinationLinkFirstLeg!.Id.Should().Be("ch-5");
        evt.DestinationLinkSecondLeg!.Id.Should().Be("ch-6");
        evt.DestinationThreewayChannel!.Id.Should().Be("ch-7");
        evt.DestinationThreewayBridge!.Id.Should().Be("br-3");
        evt.IsExternal.Should().BeTrue();
        evt.ReplaceChannel!.Id.Should().Be("ch-8");
    }

    [Fact]
    public void ChannelUsereventEvent_ShouldDeserialize_WithAllFields()
    {
        const string json = """
        {
            "type": "ChannelUserevent",
            "eventname": "MyCustomEvent",
            "channel": { "id": "ch-1" },
            "bridge": { "id": "br-1" },
            "endpoint": { "technology": "PJSIP", "resource": "2000" }
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.ChannelUsereventEvent);

        evt.Should().NotBeNull();
        evt!.Eventname.Should().Be("MyCustomEvent");
        evt.Channel!.Id.Should().Be("ch-1");
        evt.Bridge!.Id.Should().Be("br-1");
        evt.Endpoint!.Technology.Should().Be("PJSIP");
    }

    [Fact]
    public void BridgeBlindTransferEvent_ShouldDeserialize_WithReplaceChannel()
    {
        const string json = """
        {
            "type": "BridgeBlindTransfer",
            "result": "Success",
            "transferer": { "id": "ch-1" },
            "bridge": { "id": "br-1" },
            "transferee": { "id": "ch-2" },
            "replace_channel": { "id": "ch-3" },
            "context": "default",
            "exten": "200",
            "is_external": false
        }
        """;

        var evt = JsonSerializer.Deserialize(json, AriJsonContext.Default.BridgeBlindTransferEvent);

        evt.Should().NotBeNull();
        evt!.Result.Should().Be("Success");
        evt.Transferer!.Id.Should().Be("ch-1");
        evt.Bridge!.Id.Should().Be("br-1");
        evt.Transferee!.Id.Should().Be("ch-2");
        evt.ReplaceChannel!.Id.Should().Be("ch-3");
        evt.Context.Should().Be("default");
        evt.Exten.Should().Be("200");
    }
}

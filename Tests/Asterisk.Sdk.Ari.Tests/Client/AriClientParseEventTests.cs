using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Events;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Client;

public class AriClientParseEventTests
{
    [Fact]
    public void ParseEvent_ShouldReturnStasisStartEvent()
    {
        const string json = """{"type":"StasisStart","application":"myapp","channel":{"id":"ch-1","name":"PJSIP/2000","state":"Up"},"args":["arg1"]}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<StasisStartEvent>();
        var start = (StasisStartEvent)evt!;
        start.Type.Should().Be("StasisStart");
        start.Channel!.Id.Should().Be("ch-1");
        start.Args.Should().Contain("arg1");
        start.RawJson.Should().Be(json);
    }

    [Fact]
    public void ParseEvent_ShouldReturnStasisEndEvent()
    {
        const string json = """{"type":"StasisEnd","channel":{"id":"ch-2","name":"PJSIP/3000","state":"Down"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<StasisEndEvent>();
        ((StasisEndEvent)evt!).Channel!.Id.Should().Be("ch-2");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelStateChangeEvent()
    {
        const string json = """{"type":"ChannelStateChange","channel":{"id":"ch-3","name":"PJSIP/1000","state":"Ring"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelStateChangeEvent>();
        ((ChannelStateChangeEvent)evt!).Channel!.State.Should().Be(AriChannelState.Ring);
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelDtmfReceivedEvent()
    {
        const string json = """{"type":"ChannelDtmfReceived","channel":{"id":"ch-4"},"digit":"5","duration_ms":100}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelDtmfReceivedEvent>();
        var dtmf = (ChannelDtmfReceivedEvent)evt!;
        dtmf.Digit.Should().Be("5");
        dtmf.DurationMs.Should().Be(100);
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelCreatedEvent()
    {
        const string json = """{"type":"ChannelCreated","channel":{"id":"ch-new","name":"PJSIP/5000","state":"Down"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelCreatedEvent>();
        ((ChannelCreatedEvent)evt!).Channel!.Id.Should().Be("ch-new");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelDestroyedEvent()
    {
        const string json = """{"type":"ChannelDestroyed","channel":{"id":"ch-gone"},"cause":16,"cause_txt":"Normal Clearing"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelDestroyedEvent>();
        var destroyed = (ChannelDestroyedEvent)evt!;
        destroyed.Cause.Should().Be(16);
        destroyed.CauseTxt.Should().Be("Normal Clearing");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelVarsetEvent()
    {
        const string json = """{"type":"ChannelVarset","channel":{"id":"ch-1"},"variable":"CDR(src)","value":"2000"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelVarsetEvent>();
        var varset = (ChannelVarsetEvent)evt!;
        varset.Variable.Should().Be("CDR(src)");
        varset.Value.Should().Be("2000");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelHoldEvent()
    {
        const string json = """{"type":"ChannelHold","channel":{"id":"ch-1"},"music_class":"default"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelHoldEvent>();
        ((ChannelHoldEvent)evt!).MusicClass.Should().Be("default");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelUnholdEvent()
    {
        const string json = """{"type":"ChannelUnhold","channel":{"id":"ch-1"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelUnholdEvent>();
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelTalkingFinishedEvent()
    {
        const string json = """{"type":"ChannelTalkingFinished","channel":{"id":"ch-1"},"duration":5000}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelTalkingFinishedEvent>();
        ((ChannelTalkingFinishedEvent)evt!).Duration.Should().Be(5000);
    }

    [Fact]
    public void ParseEvent_ShouldReturnBridgeCreatedEvent()
    {
        const string json = """{"type":"BridgeCreated","bridge":{"id":"br-1","technology":"simple_bridge","bridge_type":"mixing"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<BridgeCreatedEvent>();
        ((BridgeCreatedEvent)evt!).Bridge!.Id.Should().Be("br-1");
    }

    [Fact]
    public void ParseEvent_ShouldReturnDialEvent()
    {
        const string json = """{"type":"Dial","peer":{"id":"ch-peer"},"caller":{"id":"ch-caller"},"dialstatus":"ANSWER"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<DialEvent>();
        var dial = (DialEvent)evt!;
        dial.Dialstatus.Should().Be("ANSWER");
        dial.Caller!.Id.Should().Be("ch-caller");
    }

    [Fact]
    public void ParseEvent_ShouldReturnRecordingStartedEvent()
    {
        const string json = """{"type":"RecordingStarted","recording":{"name":"rec-1","format":"wav","state":"recording"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<RecordingStartedEvent>();
        ((RecordingStartedEvent)evt!).Recording!.Name.Should().Be("rec-1");
    }

    [Fact]
    public void ParseEvent_ShouldReturnEndpointStateChangeEvent()
    {
        const string json = """{"type":"EndpointStateChange","endpoint":{"technology":"PJSIP","resource":"2000","state":"online"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<EndpointStateChangeEvent>();
        ((EndpointStateChangeEvent)evt!).Endpoint!.Technology.Should().Be("PJSIP");
    }

    [Fact]
    public void ParseEvent_ShouldFallbackToBaseAriEvent_ForUnknownType()
    {
        const string json = """{"type":"UnknownFutureEvent","application":"myapp","timestamp":"2026-03-04T10:00:00+00:00"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().NotBeNull();
        evt.Should().BeOfType<AriEvent>();
        evt!.Type.Should().Be("UnknownFutureEvent");
        evt.Application.Should().Be("myapp");
        evt.RawJson.Should().Be(json);
    }

    [Fact]
    public void ParseEvent_ShouldReturnNull_ForInvalidJson()
    {
        var evt = AriClient.ParseEvent("not json");

        evt.Should().BeNull();
    }
}

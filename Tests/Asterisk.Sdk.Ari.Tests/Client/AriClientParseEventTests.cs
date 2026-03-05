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

    // Sprint 1 — Transfer and recording events

    [Fact]
    public void ParseEvent_ShouldReturnBridgeAttendedTransferEvent()
    {
        const string json = """{"type":"BridgeAttendedTransfer","result":"Success","transferer_first_leg":{"id":"ch-1"},"transferer_second_leg":{"id":"ch-2"},"transferee":{"id":"ch-3"},"transfer_target":{"id":"ch-4"},"destination_type":"bridge","is_external":false}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<BridgeAttendedTransferEvent>();
        var xfer = (BridgeAttendedTransferEvent)evt!;
        xfer.Result.Should().Be("Success");
        xfer.TransfererFirstLeg!.Id.Should().Be("ch-1");
        xfer.TransfererSecondLeg!.Id.Should().Be("ch-2");
        xfer.Transferee!.Id.Should().Be("ch-3");
        xfer.TransferTarget!.Id.Should().Be("ch-4");
        xfer.DestinationType.Should().Be("bridge");
        xfer.IsExternal.Should().BeFalse();
    }

    [Fact]
    public void ParseEvent_ShouldReturnBridgeBlindTransferEvent()
    {
        const string json = """{"type":"BridgeBlindTransfer","result":"Success","transferer":{"id":"ch-1"},"bridge":{"id":"br-1"},"context":"default","exten":"5000","is_external":true}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<BridgeBlindTransferEvent>();
        var xfer = (BridgeBlindTransferEvent)evt!;
        xfer.Result.Should().Be("Success");
        xfer.Transferer!.Id.Should().Be("ch-1");
        xfer.Bridge!.Id.Should().Be("br-1");
        xfer.Context.Should().Be("default");
        xfer.Exten.Should().Be("5000");
        xfer.IsExternal.Should().BeTrue();
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelTransferEvent()
    {
        const string json = """{"type":"ChannelTransfer","channel":{"id":"ch-1","name":"PJSIP/2000","state":"Up"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelTransferEvent>();
        ((ChannelTransferEvent)evt!).Channel!.Id.Should().Be("ch-1");
    }

    [Fact]
    public void ParseEvent_ShouldReturnBridgeMergedEvent()
    {
        const string json = """{"type":"BridgeMerged","bridge":{"id":"br-1","technology":"simple_bridge","bridge_type":"mixing"},"bridge_from":{"id":"br-2","technology":"simple_bridge","bridge_type":"mixing"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<BridgeMergedEvent>();
        var merged = (BridgeMergedEvent)evt!;
        merged.Bridge!.Id.Should().Be("br-1");
        merged.BridgeFrom!.Id.Should().Be("br-2");
    }

    [Fact]
    public void ParseEvent_ShouldReturnBridgeVideoSourceChangedEvent()
    {
        const string json = """{"type":"BridgeVideoSourceChanged","bridge":{"id":"br-1"},"old_video_source_id":"ch-old"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<BridgeVideoSourceChangedEvent>();
        var video = (BridgeVideoSourceChangedEvent)evt!;
        video.Bridge!.Id.Should().Be("br-1");
        video.OldVideoSourceId.Should().Be("ch-old");
    }

    [Fact]
    public void ParseEvent_ShouldReturnRecordingFailedEvent()
    {
        const string json = """{"type":"RecordingFailed","recording":{"name":"rec-fail","format":"wav","state":"failed"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<RecordingFailedEvent>();
        var rec = (RecordingFailedEvent)evt!;
        rec.Recording!.Name.Should().Be("rec-fail");
        rec.Recording.State.Should().Be("failed");
    }

    // Sprint 3 — Complementary ARI events and TechCause field

    [Fact]
    public void ParseEvent_ShouldReturnChannelCallerIdEvent()
    {
        const string json = """{"type":"ChannelCallerId","channel":{"id":"ch-1"},"caller_presentation":0,"caller_presentation_txt":"Allowed"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelCallerIdEvent>();
        var cid = (ChannelCallerIdEvent)evt!;
        cid.CallerPresentation.Should().Be(0);
        cid.CallerPresentationTxt.Should().Be("Allowed");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelDialplanEvent()
    {
        const string json = """{"type":"ChannelDialplan","channel":{"id":"ch-1"},"dialplan_app":"Dial","dialplan_app_data":"PJSIP/3000"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelDialplanEvent>();
        var dp = (ChannelDialplanEvent)evt!;
        dp.DialplanApp.Should().Be("Dial");
        dp.DialplanAppData.Should().Be("PJSIP/3000");
    }

    [Fact]
    public void ParseEvent_ShouldReturnChannelUsereventEvent()
    {
        const string json = """{"type":"ChannelUserevent","eventname":"MyCustomEvent","channel":{"id":"ch-1"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelUsereventEvent>();
        ((ChannelUsereventEvent)evt!).Eventname.Should().Be("MyCustomEvent");
    }

    [Fact]
    public void ParseEvent_ShouldReturnDeviceStateChangedEvent()
    {
        const string json = """{"type":"DeviceStateChanged","device_state":{"name":"PJSIP/2000","state":"NOT_INUSE"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<DeviceStateChangedEvent>();
        var ds = (DeviceStateChangedEvent)evt!;
        ds.DeviceState!.Name.Should().Be("PJSIP/2000");
        ds.DeviceState.State.Should().Be("NOT_INUSE");
    }

    [Fact]
    public void ParseEvent_ShouldReturnPlaybackContinuingEvent()
    {
        const string json = """{"type":"PlaybackContinuing","playback":{"id":"pb-1","media_uri":"sound:hello","state":"continuing","target_uri":"channel:ch-1"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<PlaybackContinuingEvent>();
        ((PlaybackContinuingEvent)evt!).Playback!.Id.Should().Be("pb-1");
    }

    [Fact]
    public void ParseEvent_ShouldReturnContactStatusChangeEvent()
    {
        const string json = """{"type":"ContactStatusChange","contact_info":{"uri":"sip:2000@192.168.1.10","contact_status":"Reachable","aor":"2000"},"endpoint":{"technology":"PJSIP","resource":"2000"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ContactStatusChangeEvent>();
        var cs = (ContactStatusChangeEvent)evt!;
        cs.ContactInfo!.ContactStatus.Should().Be("Reachable");
        cs.Endpoint!.Resource.Should().Be("2000");
    }

    [Fact]
    public void ParseEvent_ShouldReturnPeerStatusChangeEvent()
    {
        const string json = """{"type":"PeerStatusChange","peer":{"peer_status":"Reachable","address":"192.168.1.10","port":"5060"},"endpoint":{"technology":"PJSIP","resource":"2000"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<PeerStatusChangeEvent>();
        var ps = (PeerStatusChangeEvent)evt!;
        ps.Peer!.PeerStatus.Should().Be("Reachable");
        ps.Peer.Address.Should().Be("192.168.1.10");
    }

    [Fact]
    public void ParseEvent_ShouldReturnTextMessageReceivedEvent()
    {
        const string json = """{"type":"TextMessageReceived","message":{"from":"pjsip:2000","to":"pjsip:3000","body":"Hello"},"endpoint":{"technology":"PJSIP","resource":"2000"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<TextMessageReceivedEvent>();
        var msg = (TextMessageReceivedEvent)evt!;
        msg.Message!.Body.Should().Be("Hello");
        msg.Message.From.Should().Be("pjsip:2000");
    }

    [Fact]
    public void ParseEvent_ShouldParseTechCause_OnChannelHangupRequest()
    {
        const string json = """{"type":"ChannelHangupRequest","channel":{"id":"ch-1"},"cause":16,"tech_cause":"487"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelHangupRequestEvent>();
        var hr = (ChannelHangupRequestEvent)evt!;
        hr.Cause.Should().Be(16);
        hr.TechCause.Should().Be("487");
    }

    [Fact]
    public void ParseEvent_ShouldParseTechCause_OnChannelDestroyed()
    {
        const string json = """{"type":"ChannelDestroyed","channel":{"id":"ch-1"},"cause":16,"cause_txt":"Normal Clearing","tech_cause":"200"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ChannelDestroyedEvent>();
        var cd = (ChannelDestroyedEvent)evt!;
        cd.TechCause.Should().Be("200");
        cd.CauseTxt.Should().Be("Normal Clearing");
    }

    // Sprint 5 — ARI events for Asterisk 12-22+

    [Fact]
    public void ParseEvent_ShouldReturnApplicationReplacedEvent()
    {
        const string json = """{"type":"ApplicationReplaced","application":"myapp"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ApplicationReplacedEvent>();
        evt!.Application.Should().Be("myapp");
    }

    [Fact]
    public void ParseEvent_ShouldReturnApplicationMoveFailedEvent()
    {
        const string json = """{"type":"ApplicationMoveFailed","channel":{"id":"ch-1"},"destination":"other-app","args":["a1"]}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ApplicationMoveFailedEvent>();
        var amf = (ApplicationMoveFailedEvent)evt!;
        amf.Channel!.Id.Should().Be("ch-1");
        amf.Destination.Should().Be("other-app");
        amf.Args.Should().Contain("a1");
    }

    [Fact]
    public void ParseEvent_ShouldReturnApplicationRegisteredEvent()
    {
        const string json = """{"type":"ApplicationRegistered","registered_application":{"name":"my-app"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ApplicationRegisteredEvent>();
        var ar = (ApplicationRegisteredEvent)evt!;
        ar.RegisteredApplication!.Name.Should().Be("my-app");
    }

    [Fact]
    public void ParseEvent_ShouldReturnApplicationUnregisteredEvent()
    {
        const string json = """{"type":"ApplicationUnregistered","unregistered_application":{"name":"old-app"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ApplicationUnregisteredEvent>();
        ((ApplicationUnregisteredEvent)evt!).UnregisteredApplication!.Name.Should().Be("old-app");
    }

    [Fact]
    public void ParseEvent_ShouldReturnMissingParamsEvent()
    {
        const string json = """{"type":"MissingParams","params":["channelId","endpoint"]}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<MissingParamsEvent>();
        var mp = (MissingParamsEvent)evt!;
        mp.Params.Should().HaveCount(2);
        mp.Params.Should().Contain("channelId");
    }

    [Fact]
    public void ParseEvent_ShouldReturnReferToEvent()
    {
        const string json = """{"type":"ReferTo","channel":{"id":"ch-1"},"refer_to":"sip:3000@pbx","referred_by":"sip:2000@pbx"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ReferToEvent>();
        var rt = (ReferToEvent)evt!;
        rt.Channel!.Id.Should().Be("ch-1");
        rt.ReferTo.Should().Be("sip:3000@pbx");
        rt.ReferredBy.Should().Be("sip:2000@pbx");
    }

    [Fact]
    public void ParseEvent_ShouldReturnReferredByEvent()
    {
        const string json = """{"type":"ReferredBy","channel":{"id":"ch-2"},"referred_by":"sip:2000@pbx"}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<ReferredByEvent>();
        ((ReferredByEvent)evt!).ReferredBy.Should().Be("sip:2000@pbx");
    }

    [Fact]
    public void ParseEvent_ShouldReturnRequiredDestinationEvent()
    {
        const string json = """{"type":"RequiredDestination","channel":{"id":"ch-3"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().BeOfType<RequiredDestinationEvent>();
        ((RequiredDestinationEvent)evt!).Channel!.Id.Should().Be("ch-3");
    }
}

using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Ari.Resources;
using Verbara.Sdk.Ari.Tests.Resources;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// A channel, a hold, a blind transfer and <c>/asterisk/info</c> as Asterisk writes them. Each frame and
/// body is captured verbatim; where a state Asterisk can write never came up on PJSIP (<c>Dialing Offhook</c>,
/// <c>Pre-ring</c>, <c>Mute</c>, <c>Unknown (N)</c>, written by <c>ast_state2str</c> for analog and DAHDI
/// channels), it is injected into a captured frame in place of the captured state.
/// </summary>
public sealed class AriChannelWireTests
{
    // Captured verbatim from Asterisk 20.20.1 over /ari/events (ari.conf has channelvars = LABVAR).
    private const string CapturedStasisStart =
        """
        {"type":"StasisStart","timestamp":"2026-10-31T22:33:59.780+0000","args":["rtp"],"channel":{"id":"lab-rtp-0","name":"PJSIP/loop-00000014","state":"Up","protocol_id":"74b28d63-ab40-4383-9eba-b30271888fb8","caller":{"name":"","number":"5550000"},"connected":{"name":"","number":"5550000"},"accountcode":"","dialplan":{"context":"lab","exten":"s","priority":1,"app_name":"Stasis","app_data":"cap,rtp"},"creationtime":"2026-10-31T22:33:59.779+0000","language":"en","channelvars":{"LABVAR":"aside-rtp-0"}},"asterisk_id":"4a:83:66:b4:b3:98","application":"cap"}
        """;

    private const string CapturedChannelStateChange =
        """
        {"type":"ChannelStateChange","timestamp":"2026-10-31T22:33:59.765+0000","channel":{"id":"1793486039.1","name":"PJSIP/loop-00000001","state":"Busy","protocol_id":"d568eaad-faa6-4f24-a46d-93282dd8339d","caller":{"name":"","number":"7100200"},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"lab","exten":"1002","priority":1,"app_name":"Busy","app_data":"5"},"creationtime":"2026-10-31T22:33:59.765+0000","language":"en","channelvars":{"LABVAR":""}},"asterisk_id":"4a:83:66:b4:b3:98","application":"cap"}
        """;

    // Captured verbatim from Asterisk 20.20.1: GET /ari/channels/lab-rtp-0.
    private const string CapturedChannelReply =
        """
        {"id":"lab-rtp-0","name":"PJSIP/loop-00000014","state":"Up","protocol_id":"74b28d63-ab40-4383-9eba-b30271888fb8","caller":{"name":"","number":"5550000"},"connected":{"name":"","number":"5550000"},"accountcode":"","dialplan":{"context":"lab","exten":"s","priority":1,"app_name":"Stasis","app_data":"cap,rtp"},"creationtime":"2026-10-31T22:33:59.779+0000","language":"en","channelvars":{"LABVAR":"aside-rtp-0"}}
        """;

    // Captured verbatim from Asterisk 20.20.1 and 24.0.0-rc2: POST /channels/{id}/hold with music on hold.
    private const string CapturedChannelHold =
        """
        {"type":"ChannelHold","timestamp":"2026-10-31T22:34:55.425+0000","musicclass":"default","channel":{"id":"1793486094.76","name":"PJSIP/loop-0000004b","state":"Up","protocol_id":"6240e64c-b1e7-4d70-bf5c-d839a89438ef","caller":{"name":"","number":"5552000"},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"lab","exten":"1005","priority":3,"app_name":"Stasis","app_data":"cap,bside"},"creationtime":"2026-10-31T22:34:54.922+0000","language":"en","channelvars":{"LABVAR":"bside-1005"}},"asterisk_id":"4a:83:66:b4:b3:98","application":"cap"}
        """;

    private const string CapturedChannelHold24 =
        """
        {"type":"ChannelHold","timestamp":"2026-10-31T22:50:24.408+0000","musicclass":"default","channel":{"id":"1793487023.76","name":"PJSIP/loop-0000004b","state":"Up","protocol_id":"2f35cd8c-23d4-4c50-956d-481bb1612f70","caller":{"name":"","number":"5552000"},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"lab","exten":"1005","priority":3,"app_name":"Stasis","app_data":"cap,bside"},"creationtime":"2026-10-31T22:50:23.906+0000","language":"en","channelvars":{"LABVAR":"bside-1005"}},"asterisk_id":"72:64:4a:ce:aa:24","application":"cap"}
        """;

    // Captured verbatim from Asterisk 20.20.1: a blind transfer inside a Stasis bridge. The transferring
    // channel arrives in "channel"; Asterisk never writes "transferer".
    private const string CapturedBridgeBlindTransfer =
        """
        {"type":"BridgeBlindTransfer","timestamp":"2026-10-31T22:35:36.501+0000","channel":{"id":"1793486134.86","name":"PJSIP/loop-00000055","state":"Up","protocol_id":"a88ebbcb-62a3-4565-a57b-4f84182260fd","caller":{"name":"","number":"5553000"},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"lab","exten":"1005","priority":3,"app_name":"Stasis","app_data":"cap,bside"},"creationtime":"2026-10-31T22:35:34.996+0000","language":"en","channelvars":{"LABVAR":"bside-1005"}},"exten":"4001","context":"lab","result":"Success","is_external":true,"transferee":{"id":"lab-xl-0","name":"Local/9001@lab-00000000;1","state":"Up","protocol_id":"","caller":{"name":"","number":""},"connected":{"name":"","number":"5553000"},"accountcode":"","dialplan":{"context":"lab","exten":"9001","priority":1,"app_name":"Stasis","app_data":"cap,xfer-local"},"creationtime":"2026-10-31T22:35:35.498+0000","language":"en","channelvars":{"LABVAR":""}},"replace_channel":{"id":"1793486136.89","name":"Local/4001@lab-00000001;1","state":"Down","protocol_id":"","caller":{"name":"","number":""},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"lab","exten":"4001","priority":1,"app_name":"","app_data":""},"creationtime":"2026-10-31T22:35:36.500+0000","language":"en","channelvars":{"LABVAR":""}},"bridge":{"id":"lab-br-0","technology":"simple_bridge","bridge_type":"mixing","bridge_class":"stasis","creator":"Stasis","name":"","channels":["1793486134.86","lab-xl-0"],"creationtime":"2026-10-31T22:35:35.499+0000","video_mode":"talker"},"asterisk_id":"4a:83:66:b4:b3:98","application":"cap"}
        """;

    // Captured verbatim from Asterisk 20.20.1: GET /ari/asterisk/info?only=config.
    private const string CapturedInfoConfig =
        """{"config":{"name":"","default_language":"en","setid":{"user":"","group":""}}}""";

    private static readonly AriClientOptions Options = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp",
    };

    private static string WithState(string frame, string capturedState, string state)
    {
        var from = $"\"state\":\"{capturedState}\"";
        frame.Should().Contain(from);
        var at = frame.IndexOf(from, StringComparison.Ordinal);
        return string.Concat(frame.AsSpan(0, at), $"\"state\":\"{state}\"", frame.AsSpan(at + from.Length));
    }

    // --- states ---

    [Theory]
    [InlineData("Dialing Offhook", AriChannelState.DialingOffhook)]
    [InlineData("Pre-ring", AriChannelState.PreRing)]
    [InlineData("Rsrved", AriChannelState.Rsrvd)]
    [InlineData("Rsrvd", AriChannelState.Rsrvd)]
    [InlineData("OffHook", AriChannelState.OffHook)]
    [InlineData("Dialing", AriChannelState.Dialing)]
    public void ParseEvent_ShouldDeliverTheStateChange_WithEveryStateAsteriskWrites(string wire, AriChannelState expected)
    {
        var evt = AriClient.ParseEvent(WithState(CapturedChannelStateChange, "Busy", wire));

        var change = evt.Should().BeOfType<ChannelStateChangeEvent>().Subject;
        change.Channel!.State.Should().Be(expected);
        change.Channel.Id.Should().Be("1793486039.1");
    }

    [Theory]
    [InlineData("Unknown (7)")]
    [InlineData("Unknown (4294967295)")]
    [InlineData("Some Future State")]
    [InlineData("")]
    public void ParseEvent_ShouldDeliverStasisStart_WithStateUnknown_WhenTheStateIsOneTheSdkDoesNotKnow(string wire)
    {
        var evt = AriClient.ParseEvent(WithState(CapturedStasisStart, "Up", wire));

        var start = evt.Should().BeOfType<StasisStartEvent>().Subject;
        start.Channel!.State.Should().Be(AriChannelState.Unknown);
        start.Channel.Id.Should().Be("lab-rtp-0");
        start.Channel.Name.Should().Be("PJSIP/loop-00000014");
        start.Application.Should().Be("cap");
    }

    [Fact]
    public void ParseEvent_ShouldReadTheCapturedState()
    {
        var evt = AriClient.ParseEvent(CapturedStasisStart);

        evt.Should().BeOfType<StasisStartEvent>().Which.Channel!.State.Should().Be(AriChannelState.Up);
    }

    [Theory]
    [InlineData("up", AriChannelState.Up)]
    [InlineData("DialingOffhook", AriChannelState.DialingOffhook)]
    [InlineData("PreRing", AriChannelState.PreRing)]
    [InlineData("pre-ring", AriChannelState.PreRing)]
    public void Deserialize_ShouldKeepReadingTheSpellingsTheEarlierReaderAccepted(string wire, AriChannelState expected)
    {
        var channel = JsonSerializer.Deserialize(
            WithState(CapturedChannelReply, "Up", wire), AriJsonContext.Default.AriChannel);

        channel!.State.Should().Be(expected);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEveryChannel_WhenOneStateIsUnknownToTheSdk()
    {
        var other = WithState(CapturedChannelReply, "Up", "Some Future State").Replace("lab-rtp-0", "lab-rtp-1");
        using var handler = new FakeHttpHandler($"[{CapturedChannelReply},{other}]");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };
        var sut = new AriChannelsResource(http, Options);

        var channels = await sut.ListAsync();

        channels.Select(c => (c.Id, c.State)).Should().Equal(
            ("lab-rtp-0", AriChannelState.Up), ("lab-rtp-1", AriChannelState.Unknown));
    }

    // --- names ---

    [Fact]
    public void Deserialize_ShouldReadTheProtocolIdAndTheChannelVariables_FromACapturedChannelReply()
    {
        var channel = JsonSerializer.Deserialize(CapturedChannelReply, AriJsonContext.Default.AriChannel);

        channel!.Protocol.Should().Be("74b28d63-ab40-4383-9eba-b30271888fb8");
        channel.ChannelVars.Should().Equal(new Dictionary<string, string> { ["LABVAR"] = "aside-rtp-0" });
    }

    [Fact]
    public void ParseEvent_ShouldReadTheProtocolIdAndTheChannelVariables_OfAnEventsChannel()
    {
        var evt = AriClient.ParseEvent(CapturedStasisStart);

        var channel = evt.Should().BeOfType<StasisStartEvent>().Subject.Channel!;
        channel.Protocol.Should().Be("74b28d63-ab40-4383-9eba-b30271888fb8");
        channel.ChannelVars.Should().ContainKey("LABVAR").WhoseValue.Should().Be("aside-rtp-0");
    }

    [Theory]
    [InlineData(CapturedChannelHold)]
    [InlineData(CapturedChannelHold24)]
    public void ParseEvent_ShouldReadTheMusicClass_OfACapturedChannelHold(string frame)
    {
        var evt = AriClient.ParseEvent(frame);

        evt.Should().BeOfType<ChannelHoldEvent>().Which.MusicClass.Should().Be("default");
    }

    [Fact]
    public async Task GetInfoAsync_ShouldReadTheSetId_OfTheCapturedConfigReply()
    {
        using var handler = new FakeHttpHandler(CapturedInfoConfig);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };
        var sut = new AriAsteriskResource(http);

        var info = await sut.GetInfoAsync();

        info.Config.Should().NotBeNull();
        info.Config!.DefaultLanguage.Should().Be("en");
        info.Config.SetId.Should().NotBeNull();
        info.Config.SetId!.User.Should().BeEmpty();
        info.Config.SetId.Group.Should().BeEmpty();
    }

    [Fact]
    public void ParseEvent_ShouldReadTheTransferringChannelFromChannel_AndTheTransfereeFromTransferee()
    {
        var evt = AriClient.ParseEvent(CapturedBridgeBlindTransfer);

        var transfer = evt.Should().BeOfType<BridgeBlindTransferEvent>().Subject;
        transfer.Transferer!.Id.Should().Be("1793486134.86");
        transfer.Transferee!.Id.Should().Be("lab-xl-0");
        transfer.ReplaceChannel!.Id.Should().Be("1793486136.89");
        transfer.Bridge!.Id.Should().Be("lab-br-0");
        transfer.Exten.Should().Be("4001");
        transfer.IsExternal.Should().BeTrue();
    }
}

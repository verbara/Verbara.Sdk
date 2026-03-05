using System.Net;
using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public class AriResourceTests
{
    private static readonly AriClientOptions DefaultOptions = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp"
    };

    private static HttpClient CreateHttpClient(FakeHttpHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8088/ari/")
        };
        return client;
    }

    // --- Channels ---

    [Fact]
    public async Task Channels_ListAsync_ShouldReturnChannels()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriChannel { Id = "ch-1", Name = "PJSIP/2000", State = AriChannelState.Up } },
            AriJsonContext.Default.AriChannelArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.ListAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("ch-1");
        handler.LastRequestUri.Should().Contain("channels");
    }

    [Fact]
    public async Task Channels_GetAsync_ShouldReturnChannel()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-42", Name = "SIP/100", State = AriChannelState.Ring },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.GetAsync("ch-42");

        result.Id.Should().Be("ch-42");
        result.State.Should().Be(AriChannelState.Ring);
        handler.LastRequestUri.Should().Contain("channels/ch-42");
    }

    [Fact]
    public async Task Channels_CreateAsync_ShouldPostAndReturnChannel()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-new", Name = "PJSIP/3000", State = AriChannelState.Down },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.CreateAsync("PJSIP/3000");

        result.Id.Should().Be("ch-new");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("endpoint=PJSIP%2F3000");
    }

    [Fact]
    public async Task Channels_HangupAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.HangupAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1");
    }

    [Fact]
    public async Task Channels_OriginateAsync_ShouldIncludeQueryParams()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-orig", Name = "PJSIP/4000" },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.OriginateAsync("PJSIP/4000", extension: "100", context: "default");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("extension=100");
        handler.LastRequestUri.Should().Contain("context=default");
    }

    [Fact]
    public async Task Channels_RingAsync_ShouldPostRing()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.RingAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/ring");
    }

    [Fact]
    public async Task Channels_ProgressAsync_ShouldPostProgress()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.ProgressAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/progress");
    }

    [Fact]
    public async Task Channels_AnswerAsync_ShouldPostAnswer()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.AnswerAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/answer");
    }

    [Fact]
    public async Task Channels_CreateExternalMediaAsync_ShouldPostWithParams()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-ext", Name = "UnicastRTP/127.0.0.1:8000" },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.CreateExternalMediaAsync("myapp", "127.0.0.1:8000", "slin16",
            encapsulation: "rtp", transport: "udp");

        result.Id.Should().Be("ch-ext");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("externalMedia");
        handler.LastRequestUri.Should().Contain("app=myapp");
        handler.LastRequestUri.Should().Contain("format=slin16");
        handler.LastRequestUri.Should().Contain("encapsulation=rtp");
        handler.LastRequestUri.Should().Contain("transport=udp");
    }

    [Fact]
    public async Task Channels_GetAsync_ShouldDeserializeFullModel()
    {
        const string json = """
        {
            "id": "ch-full",
            "name": "PJSIP/2000-00000001",
            "state": "Up",
            "caller": { "name": "Alice", "number": "2000" },
            "connected": { "name": "Bob", "number": "3000" },
            "accountcode": "sales",
            "dialplan": { "context": "default", "exten": "100", "priority": 1, "app_name": "Stasis", "app_data": "myapp" },
            "language": "en",
            "creationtime": "2026-03-04T10:30:00+00:00",
            "protocol": "PJSIP",
            "channel_vars": { "CDR(src)": "2000" }
        }
        """;
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.GetAsync("ch-full");

        result.Id.Should().Be("ch-full");
        result.State.Should().Be(AriChannelState.Up);
        result.Caller.Should().NotBeNull();
        result.Caller!.Name.Should().Be("Alice");
        result.Caller.Number.Should().Be("2000");
        result.Connected.Should().NotBeNull();
        result.Connected!.Number.Should().Be("3000");
        result.Accountcode.Should().Be("sales");
        result.Dialplan.Should().NotBeNull();
        result.Dialplan!.Context.Should().Be("default");
        result.Dialplan.Exten.Should().Be("100");
        result.Dialplan.Priority.Should().Be(1);
        result.Dialplan.AppName.Should().Be("Stasis");
        result.Language.Should().Be("en");
        result.Creationtime.Should().NotBeNull();
        result.Protocol.Should().Be("PJSIP");
        result.ChannelVars.Should().ContainKey("CDR(src)");
    }

    [Fact]
    public async Task Channels_GetAsync_ShouldHandleNullOptionalFields()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-min", Name = "PJSIP/1000", State = AriChannelState.Down },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.GetAsync("ch-min");

        result.Id.Should().Be("ch-min");
        result.Caller.Should().BeNull();
        result.Connected.Should().BeNull();
        result.Dialplan.Should().BeNull();
        result.ChannelVars.Should().BeNull();
    }

    [Fact]
    public async Task Channels_GetVariableAsync_ShouldReturnVariable()
    {
        var json = JsonSerializer.Serialize(
            new AriVariable { Value = "bar" },
            AriJsonContext.Default.AriVariable);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.GetVariableAsync("ch-1", "foo");

        result.Value.Should().Be("bar");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("channels/ch-1/variable");
        handler.LastRequestUri.Should().Contain("variable=foo");
    }

    [Fact]
    public async Task Channels_SetVariableAsync_ShouldPostVariable()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.SetVariableAsync("ch-1", "foo", "bar");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/variable");
        handler.LastRequestUri.Should().Contain("variable=foo");
        handler.LastRequestUri.Should().Contain("value=bar");
    }

    [Fact]
    public async Task Channels_HoldAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.HoldAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("channels/ch-1/hold");
    }

    [Fact]
    public async Task Channels_UnholdAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.UnholdAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1/hold");
    }

    [Fact]
    public async Task Channels_MuteAsync_ShouldSendPutWithDirection()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.MuteAsync("ch-1", "in");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("channels/ch-1/mute");
        handler.LastRequestUri.Should().Contain("direction=in");
    }

    [Fact]
    public async Task Channels_UnmuteAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.UnmuteAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1/mute");
    }

    [Fact]
    public async Task Channels_SendDtmfAsync_ShouldPostDtmf()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.SendDtmfAsync("ch-1", "1234#", between: 100, duration: 200);

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/dtmf");
        handler.LastRequestUri.Should().Contain("dtmf=1234%23");
        handler.LastRequestUri.Should().Contain("between=100");
        handler.LastRequestUri.Should().Contain("duration=200");
    }

    [Fact]
    public async Task Channels_PlayAsync_ShouldPostAndReturnPlayback()
    {
        var json = JsonSerializer.Serialize(
            new AriPlayback { Id = "pb-1", MediaUri = "sound:hello", State = "playing" },
            AriJsonContext.Default.AriPlayback);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.PlayAsync("ch-1", "sound:hello", lang: "en");

        result.Id.Should().Be("pb-1");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/play");
        handler.LastRequestUri.Should().Contain("media=sound%3Ahello");
        handler.LastRequestUri.Should().Contain("lang=en");
    }

    [Fact]
    public async Task Channels_RecordAsync_ShouldPostAndReturnRecording()
    {
        var json = JsonSerializer.Serialize(
            new AriLiveRecording { Name = "rec-1", Format = "wav", State = "recording" },
            AriJsonContext.Default.AriLiveRecording);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.RecordAsync("ch-1", "rec-1", "wav", maxDurationSeconds: 60, beep: true);

        result.Name.Should().Be("rec-1");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/record");
        handler.LastRequestUri.Should().Contain("name=rec-1");
        handler.LastRequestUri.Should().Contain("format=wav");
        handler.LastRequestUri.Should().Contain("maxDurationSeconds=60");
        handler.LastRequestUri.Should().Contain("beep=true");
    }

    [Fact]
    public async Task Channels_SnoopAsync_ShouldPostAndReturnChannel()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "ch-snoop", Name = "Snoop/ch-1" },
            AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.SnoopAsync("ch-1", "myapp", spy: "both");

        result.Id.Should().Be("ch-snoop");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/snoop");
        handler.LastRequestUri.Should().Contain("app=myapp");
        handler.LastRequestUri.Should().Contain("spy=both");
    }

    [Fact]
    public async Task Channels_RedirectAsync_ShouldPostRedirect()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.RedirectAsync("ch-1", "PJSIP/5000");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/redirect");
        handler.LastRequestUri.Should().Contain("endpoint=PJSIP%2F5000");
    }

    [Fact]
    public async Task Channels_ContinueAsync_ShouldPostContinue()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.ContinueAsync("ch-1", context: "from-internal", extension: "200", priority: 1);

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/continue");
        handler.LastRequestUri.Should().Contain("context=from-internal");
        handler.LastRequestUri.Should().Contain("extension=200");
        handler.LastRequestUri.Should().Contain("priority=1");
    }

    // --- Bridges ---

    [Fact]
    public async Task Bridges_ListAsync_ShouldReturnBridges()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriBridge { Id = "br-1", Technology = "simple_bridge" } },
            AriJsonContext.Default.AriBridgeArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.ListAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("br-1");
    }

    [Fact]
    public async Task Bridges_CreateAsync_ShouldPostAndReturnBridge()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-new", BridgeType = "mixing" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.CreateAsync("mixing", "mybridge");

        result.Id.Should().Be("br-new");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("type=mixing");
    }

    [Fact]
    public async Task Bridges_GetAsync_ShouldReturnBridge()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-42" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.GetAsync("br-42");

        result.Id.Should().Be("br-42");
        handler.LastRequestUri.Should().Contain("bridges/br-42");
    }

    [Fact]
    public async Task Bridges_DestroyAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.DestroyAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1");
    }

    [Fact]
    public async Task Bridges_AddChannelAsync_ShouldPostWithChannelId()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.AddChannelAsync("br-1", "ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("addChannel");
        handler.LastRequestUri.Should().Contain("channel=ch-1");
    }

    [Fact]
    public async Task Bridges_RemoveChannelAsync_ShouldPostWithChannelId()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.RemoveChannelAsync("br-1", "ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("removeChannel");
        handler.LastRequestUri.Should().Contain("channel=ch-1");
    }

    // --- Playbacks ---

    [Fact]
    public async Task Playbacks_GetAsync_ShouldReturnPlayback()
    {
        var json = JsonSerializer.Serialize(
            new AriPlayback { Id = "pb-1", MediaUri = "sound:hello", State = "playing" },
            AriJsonContext.Default.AriPlayback);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriPlaybacksResource(http);

        var result = await sut.GetAsync("pb-1");

        result.Id.Should().Be("pb-1");
        result.State.Should().Be("playing");
    }

    [Fact]
    public async Task Playbacks_StopAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriPlaybacksResource(http);

        await sut.StopAsync("pb-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("playbacks/pb-1");
    }

    [Fact]
    public async Task Playbacks_ControlAsync_ShouldPostWithOperation()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriPlaybacksResource(http);

        await sut.ControlAsync("pb-1", "pause");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("control");
        handler.LastRequestUri.Should().Contain("operation=pause");
    }

    // --- Recordings ---

    [Fact]
    public async Task Recordings_GetLiveAsync_ShouldReturnRecording()
    {
        var json = JsonSerializer.Serialize(
            new AriLiveRecording { Name = "rec-1", Format = "wav", State = "recording" },
            AriJsonContext.Default.AriLiveRecording);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        var result = await sut.GetLiveAsync("rec-1");

        result.Name.Should().Be("rec-1");
        result.Format.Should().Be("wav");
    }

    [Fact]
    public async Task Recordings_StopAsync_ShouldPostStop()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.StopAsync("rec-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("recordings/live/rec-1/stop");
    }

    [Fact]
    public async Task Recordings_DeleteStoredAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.DeleteStoredAsync("rec-stored");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("recordings/stored/rec-stored");
    }

    // --- DeviceStates ---

    [Fact]
    public async Task DeviceStates_ListAsync_ShouldReturnDeviceStates()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriDeviceState { Name = "PJSIP/2000", State = "NOT_INUSE" } },
            AriJsonContext.Default.AriDeviceStateArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriDeviceStatesResource(http);

        var result = await sut.ListAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("PJSIP/2000");
        result[0].State.Should().Be("NOT_INUSE");
        handler.LastRequestUri.Should().Contain("deviceStates");
    }

    [Fact]
    public async Task DeviceStates_GetAsync_ShouldReturnDeviceState()
    {
        var json = JsonSerializer.Serialize(
            new AriDeviceState { Name = "PJSIP/3000", State = "INUSE" },
            AriJsonContext.Default.AriDeviceState);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriDeviceStatesResource(http);

        var result = await sut.GetAsync("PJSIP/3000");

        result.Name.Should().Be("PJSIP/3000");
        result.State.Should().Be("INUSE");
        handler.LastRequestUri.Should().Contain("deviceStates/PJSIP%2F3000");
    }

    [Fact]
    public async Task DeviceStates_UpdateAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriDeviceStatesResource(http);

        await sut.UpdateAsync("Custom:lamp1", "INUSE");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("deviceStates/Custom%3Alamp1");
        handler.LastRequestUri.Should().Contain("deviceState=INUSE");
    }

    [Fact]
    public async Task DeviceStates_DeleteAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriDeviceStatesResource(http);

        await sut.DeleteAsync("Custom:lamp1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("deviceStates/Custom%3Alamp1");
    }

    // --- Endpoints ---

    [Fact]
    public async Task Endpoints_ListAsync_ShouldReturnEndpoints()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriEndpoint { Technology = "PJSIP", Resource = "2000" } },
            AriJsonContext.Default.AriEndpointArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        var result = await sut.ListAsync();

        result.Should().HaveCount(1);
        result[0].Technology.Should().Be("PJSIP");
    }
}

/// <summary>
/// Fake HTTP handler that returns predefined responses for unit testing ARI resources.
/// </summary>
internal sealed class FakeHttpHandler(string responseJson) : DelegatingHandler
{
    public HttpMethod? LastMethod { get; private set; }
    public string? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri?.ToString();

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

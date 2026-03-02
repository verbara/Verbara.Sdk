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

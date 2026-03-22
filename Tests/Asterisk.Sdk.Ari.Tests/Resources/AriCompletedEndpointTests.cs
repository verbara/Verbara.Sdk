using System.Net;
using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public class AriCompletedEndpointTests
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
    public async Task Channels_MoveAsync_ShouldPostMove()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.MoveAsync("ch-1", "newapp");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("move");
        handler.LastRequestUri.Should().Contain("app=");
    }

    [Fact]
    public async Task Channels_DialAsync_ShouldPostDial()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.DialAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("dial");
    }

    [Fact]
    public async Task Channels_GetRtpStatisticsAsync_ShouldReturnStats()
    {
        var json = JsonSerializer.Serialize(
            new AriRtpStats { Txcount = 100, Rxcount = 95 },
            AriJsonContext.Default.AriRtpStats);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.GetRtpStatisticsAsync("ch-1");

        result.Txcount.Should().Be(100);
        result.Rxcount.Should().Be(95);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("rtp_statistics");
    }

    [Fact]
    public async Task Channels_SilenceAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.SilenceAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/silence");
    }

    [Fact]
    public async Task Channels_StopSilenceAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.StopSilenceAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1/silence");
    }

    [Fact]
    public async Task Channels_StartMohAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.StartMohAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("channels/ch-1/moh");
    }

    [Fact]
    public async Task Channels_StopMohAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.StopMohAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1/moh");
    }

    [Fact]
    public async Task Channels_StopRingAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.StopRingAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("channels/ch-1/ring");
    }

    // --- Bridges ---

    [Fact]
    public async Task Bridges_CreateWithIdAsync_ShouldPostWithId()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-fixed" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.CreateWithIdAsync("br-fixed", "mixing");

        result.Id.Should().Be("br-fixed");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-fixed");
    }

    [Fact]
    public async Task Bridges_SetVideoSourceAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.SetVideoSourceAsync("br-1", "ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/videoSource/ch-1");
    }

    [Fact]
    public async Task Bridges_ClearVideoSourceAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.ClearVideoSourceAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1/videoSource");
    }

    [Fact]
    public async Task Bridges_StartMohAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.StartMohAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/moh");
    }

    [Fact]
    public async Task Bridges_StopMohAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.StopMohAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1/moh");
    }

    // --- Recordings ---

    [Fact]
    public async Task Recordings_ListStoredAsync_ShouldReturnRecordings()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriStoredRecording { Name = "rec-stored-1", Format = "wav" } },
            AriJsonContext.Default.AriStoredRecordingArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        var result = await sut.ListStoredAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("rec-stored-1");
        result[0].Format.Should().Be("wav");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("recordings/stored");
    }

    [Fact]
    public async Task Recordings_GetStoredAsync_ShouldReturnRecording()
    {
        var json = JsonSerializer.Serialize(
            new AriStoredRecording { Name = "my-rec", Format = "gsm" },
            AriJsonContext.Default.AriStoredRecording);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        var result = await sut.GetStoredAsync("my-rec");

        result.Name.Should().Be("my-rec");
        result.Format.Should().Be("gsm");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("recordings/stored/my-rec");
    }

    [Fact]
    public async Task Recordings_CopyStoredAsync_ShouldPostCopy()
    {
        var json = JsonSerializer.Serialize(
            new AriStoredRecording { Name = "rec-copy", Format = "wav" },
            AriJsonContext.Default.AriStoredRecording);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        var result = await sut.CopyStoredAsync("my-rec", "rec-copy");

        result.Name.Should().Be("rec-copy");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("recordings/stored/my-rec/copy");
        handler.LastRequestUri.Should().Contain("destinationRecordingName=rec-copy");
    }

    [Fact]
    public async Task Recordings_CancelAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.CancelAsync("live-rec");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("recordings/live/live-rec");
    }

    [Fact]
    public async Task Recordings_PauseAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.PauseAsync("live-rec");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("recordings/live/live-rec/pause");
    }

    [Fact]
    public async Task Recordings_UnpauseAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.UnpauseAsync("live-rec");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("recordings/live/live-rec/pause");
    }

    [Fact]
    public async Task Recordings_MuteAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.MuteAsync("live-rec");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("recordings/live/live-rec/mute");
    }

    [Fact]
    public async Task Recordings_UnmuteAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        await sut.UnmuteAsync("live-rec");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("recordings/live/live-rec/mute");
    }

    [Fact]
    public async Task Recordings_GetStoredFileAsync_ShouldReturnStream()
    {
        using var handler = new FakeHttpHandler("audio-bytes");
        using var http = CreateHttpClient(handler);
        var sut = new AriRecordingsResource(http);

        var stream = await sut.GetStoredFileAsync("my-rec");

        stream.Should().NotBeNull();
        handler.LastRequestUri.Should().Contain("recordings/stored/my-rec/file");
    }

    // --- Applications ---

    [Fact]
    public async Task Applications_SubscribeAsync_ShouldPostAndReturn()
    {
        var json = JsonSerializer.Serialize(
            new AriApplication { Name = "myapp" },
            AriJsonContext.Default.AriApplication);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.SubscribeAsync("myapp", "channel:ch-1");

        result.Name.Should().Be("myapp");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("applications/myapp/subscription");
        handler.LastRequestUri.Should().Contain("eventSource=");
    }

    [Fact]
    public async Task Applications_UnsubscribeAsync_ShouldDeleteAndReturn()
    {
        var json = JsonSerializer.Serialize(
            new AriApplication { Name = "myapp" },
            AriJsonContext.Default.AriApplication);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.UnsubscribeAsync("myapp", "channel:ch-1");

        result.Name.Should().Be("myapp");
        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("applications/myapp/subscription");
        handler.LastRequestUri.Should().Contain("eventSource=");
    }

    [Fact]
    public async Task Applications_SetEventFilterAsync_ShouldPut()
    {
        var json = JsonSerializer.Serialize(
            new AriApplication { Name = "myapp" },
            AriJsonContext.Default.AriApplication);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.SetEventFilterAsync("myapp");

        result.Name.Should().Be("myapp");
        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("applications/myapp/eventFilter");
    }

    // --- Endpoints ---

    [Fact]
    public async Task Endpoints_ListByTechAsync_ShouldReturnEndpoints()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriEndpoint { Technology = "PJSIP", Resource = "2000" } },
            AriJsonContext.Default.AriEndpointArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        var result = await sut.ListByTechAsync("PJSIP");

        result.Should().HaveCount(1);
        result[0].Technology.Should().Be("PJSIP");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("endpoints/PJSIP");
    }

    [Fact]
    public async Task Endpoints_SendMessageAsync_ShouldPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageAsync("PJSIP/3000", "PJSIP/2000");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("endpoints/sendMessage");
        handler.LastRequestUri.Should().Contain("to=");
        handler.LastRequestUri.Should().Contain("from=");
    }

    [Fact]
    public async Task Endpoints_SendMessageToEndpointAsync_ShouldPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageToEndpointAsync("PJSIP", "3000", "PJSIP/2000");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("endpoints/PJSIP/3000/sendMessage");
        handler.LastRequestUri.Should().Contain("from=");
    }
}

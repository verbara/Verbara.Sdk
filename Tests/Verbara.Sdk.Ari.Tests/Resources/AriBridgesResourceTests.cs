using System.Net;
using System.Text.Json;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Resources;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Resources;

public sealed class AriBridgesResourceTests
{
    private static readonly AriClientOptions DefaultOptions = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp"
    };

    private static HttpClient CreateHttpClient(FakeHttpHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };

    [Fact]
    public async Task ListAsync_ShouldReturnBridges()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriBridge { Id = "br-1", BridgeType = "mixing", Technology = "simple_bridge" } },
            AriJsonContext.Default.AriBridgeArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.ListAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("br-1");
        result[0].BridgeType.Should().Be("mixing");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("bridges");
    }

    [Fact]
    public async Task CreateAsync_ShouldPostWithTypeAndName()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-new", BridgeType = "holding" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.CreateAsync(type: "holding", name: "park");

        result.Id.Should().Be("br-new");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("type=holding");
        handler.LastRequestUri.Should().Contain("name=park");
    }

    [Fact]
    public async Task CreateAsync_ShouldWorkWithoutOptionalParams()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-default" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.CreateAsync();

        result.Id.Should().Be("br-default");
        handler.LastMethod.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnBridge()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "br-42", BridgeType = "mixing", Channels = ["ch-1", "ch-2"] },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.GetAsync("br-42");

        result.Id.Should().Be("br-42");
        result.Channels.Should().HaveCount(2);
        handler.LastRequestUri.Should().Contain("bridges/br-42");
    }

    [Fact]
    public async Task DestroyAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.DestroyAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1");
    }

    [Fact]
    public async Task AddChannelAsync_ShouldPostWithChannelParam()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.AddChannelAsync("br-1", "ch-42");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/addChannel");
        handler.LastRequestUri.Should().Contain("channel=ch-42");
    }

    [Fact]
    public async Task RemoveChannelAsync_ShouldPostWithChannelParam()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.RemoveChannelAsync("br-1", "ch-42");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/removeChannel");
        handler.LastRequestUri.Should().Contain("channel=ch-42");
    }

    [Fact]
    public async Task PlayAsync_ShouldPostWithAllParams()
    {
        var json = JsonSerializer.Serialize(
            new AriPlayback { Id = "pb-1", MediaUri = "sound:hello-world", State = "playing" },
            AriJsonContext.Default.AriPlayback);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.PlayAsync("br-1", "sound:hello-world",
            lang: "en", offsetms: 100, skipms: 3000, playbackId: "pb-1");

        result.Id.Should().Be("pb-1");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/play");
        handler.LastRequestUri.Should().Contain("media=sound%3Ahello-world");
        handler.LastRequestUri.Should().Contain("lang=en");
        handler.LastRequestUri.Should().Contain("offsetms=100");
        handler.LastRequestUri.Should().Contain("skipms=3000");
        handler.LastRequestUri.Should().Contain("playbackId=pb-1");
    }

    [Fact]
    public async Task RecordAsync_ShouldPostWithRequiredAndOptionalParams()
    {
        var json = JsonSerializer.Serialize(
            new AriLiveRecording { Name = "rec-1", Format = "wav", State = "recording" },
            AriJsonContext.Default.AriLiveRecording);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.RecordAsync("br-1", "rec-1", "wav",
            maxDurationSeconds: 60, beep: true, terminateOn: "#");

        result.Name.Should().Be("rec-1");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/record");
        handler.LastRequestUri.Should().Contain("name=rec-1");
        handler.LastRequestUri.Should().Contain("format=wav");
        handler.LastRequestUri.Should().Contain("maxDurationSeconds=60");
        handler.LastRequestUri.Should().Contain("beep=true");
        handler.LastRequestUri.Should().Contain("terminateOn=%23");
    }

    // Captured from Asterisk 22.11.0 through a byte relay (lab capture run 2026-11-06-a04e8a3e, image 22.11.0, lab
    // commit 94e3d214, raw/a22/sdkproxy/proxy/conn-003.s2c @1290): the 201 body answering
    // POST /ari/bridges/{id}/play?media=tone%3A440&announcer_format=slin16. On every image measured (20.20.1 to
    // 24.0.0-rc2) announcer_format set the Announcer channel's write format and a bare format= was ignored.
    private const string CapturedBridgePlayback201 =
        "{\"id\":\"833dcc33-7238-4e81-9af1-4d42b233551c\",\"media_uri\":\"tone:440\",\"target_uri\":\"bridge:45f040b2-cefc-4dc9-9d4d-ecf5c7752ebd\",\"language\":\"en\",\"state\":\"queued\"}";

    [Fact]
    public async Task PlayAsync_ShouldSendAnnouncerFormat_WhenFormatIsGiven()
    {
        using var handler = new FakeHttpHandler(CapturedBridgePlayback201, HttpStatusCode.Created);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.PlayAsync("45f040b2-cefc-4dc9-9d4d-ecf5c7752ebd", "tone:440", format: "slin16");

        result.Id.Should().Be("833dcc33-7238-4e81-9af1-4d42b233551c");
        var pairs = QueryPairs(handler.LastRequestQuery!);
        pairs.Should().ContainSingle(p => p.Key == "announcer_format").Which.Value.Should().Be("slin16");
        pairs.Select(p => p.Key).Should().NotContain("format");
    }

    [Fact]
    public async Task PlayAsync_ShouldOmitAnnouncerFormat_WhenFormatIsNull()
    {
        using var handler = new FakeHttpHandler(CapturedBridgePlayback201, HttpStatusCode.Created);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.PlayAsync("45f040b2-cefc-4dc9-9d4d-ecf5c7752ebd", "tone:440");

        QueryPairs(handler.LastRequestQuery!).Select(p => p.Key + "=" + p.Value).Should().Equal("media=tone:440");
    }

    [Fact]
    public async Task CreateWithIdAsync_ShouldPostToBridgeIdUrl()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "my-bridge", BridgeType = "mixing" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var result = await sut.CreateWithIdAsync("my-bridge", type: "mixing", name: "conf-1");

        result.Id.Should().Be("my-bridge");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/my-bridge");
        handler.LastRequestUri.Should().Contain("type=mixing");
        handler.LastRequestUri.Should().Contain("name=conf-1");
    }

    [Fact]
    public async Task SetVideoSourceAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.SetVideoSourceAsync("br-1", "ch-video");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/videoSource/ch-video");
    }

    [Fact]
    public async Task ClearVideoSourceAsync_ShouldDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.ClearVideoSourceAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1/videoSource");
    }

    [Fact]
    public async Task StartMohAsync_ShouldPostWithOptionalClass()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.StartMohAsync("br-1", mohClass: "jazz");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/moh");
        handler.LastRequestUri.Should().Contain("mohClass=jazz");
    }

    [Fact]
    public async Task StartMohAsync_ShouldPostWithoutClass_WhenNull()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.StartMohAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("bridges/br-1/moh");
        handler.LastRequestUri.Should().NotContain("mohClass");
    }

    [Fact]
    public async Task StopMohAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.StopMohAsync("br-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("bridges/br-1/moh");
    }

    [Fact]
    public async Task GetAsync_ShouldEscapeBridgeId()
    {
        var json = JsonSerializer.Serialize(
            new AriBridge { Id = "bridge/special" },
            AriJsonContext.Default.AriBridge);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        await sut.GetAsync("bridge/special");

        handler.LastRequestUri.Should().Contain("bridges/bridge%2Fspecial");
    }

    // Splits an escaped query into unescaped key/value pairs, so a test asserts on exact keys: a substring check
    // would let "announcer_format=slin16" or "recorder_format=slin16" satisfy "format=slin16".
    private static List<KeyValuePair<string, string>> QueryPairs(string query)
    {
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0) return [];
        return trimmed.Split('&')
            .Select(pair =>
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                return eq < 0
                    ? new KeyValuePair<string, string>(Uri.UnescapeDataString(pair), "")
                    : new KeyValuePair<string, string>(
                        Uri.UnescapeDataString(pair[..eq]), Uri.UnescapeDataString(pair[(eq + 1)..]));
            })
            .ToList();
    }
}

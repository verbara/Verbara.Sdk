using System.Net;
using System.Text.Json;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Resources;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Resources;

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
    public async Task Channels_OriginateAsync_ShouldSendNoApp_WhenAnExtensionIsGiven()
    {
        var json = JsonSerializer.Serialize(new AriChannel { Id = "ch-orig", Name = "PJSIP/4000" }, AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.OriginateAsync("PJSIP/4000", extension: "100", context: "default");

        new Uri(handler.LastRequestUri!).Query.Should().Be(
            "?endpoint=PJSIP%2F4000&extension=100&context=default",
            "with app in the same request Asterisk puts the channel in Stasis and never runs the dialplan");
    }

    [Fact]
    public async Task Channels_OriginateAsync_ShouldSendTheApplication_WhenNoExtensionIsGiven()
    {
        var json = JsonSerializer.Serialize(new AriChannel { Id = "ch-orig", Name = "PJSIP/4000" }, AriJsonContext.Default.AriChannel);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.OriginateAsync("PJSIP/4000");

        new Uri(handler.LastRequestUri!).Query.Should().Be($"?endpoint=PJSIP%2F4000&app={DefaultOptions.Application}");
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

        // Every optional is SUPPLIED, not just asserted on. Until this change the test passed
        // encapsulation and transport only, and the connection_type, direction and data appenders had
        // never executed across the whole Ari suite — an assertion about a parameter the test does not
        // send holds whatever the appender does, including nothing.
        var result = await sut.CreateExternalMediaAsync("myapp", "127.0.0.1:8000", "slin16",
            encapsulation: "rtp", transport: "udp", connectionType: "client", direction: "both",
            data: "f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0",
            channelId: "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");

        result.Id.Should().Be("ch-ext");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("externalMedia");
        handler.LastRequestUri.Should().Contain("app=myapp");
        handler.LastRequestUri.Should().Contain("format=slin16");
        handler.LastRequestUri.Should().Contain("encapsulation=rtp");
        handler.LastRequestUri.Should().Contain("transport=udp");
        handler.LastRequestUri.Should().Contain("connection_type=client");
        handler.LastRequestUri.Should().Contain("direction=both");
        handler.LastRequestUri.Should().Contain("data=f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0");

        // The literal spelling, deliberately. Asterisk spells this one parameter in camelCase among
        // snake_case siblings and ignores an unrecognised query parameter silently — "channel_id="
        // would leave the create returning HTTP 200 with an Asterisk-minted id and no error anywhere,
        // so only a test on the exact bytes catches the misspelling.
        handler.LastRequestUri.Should().Contain("channelId=0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
            "Asterisk spells this parameter channelId, and silently ignores any other spelling");
        handler.LastRequestUri.Should().NotContain("channel_id=",
            "the snake_case spelling the siblings use is the misspelling this assertion exists to catch");
    }

    [Fact]
    public async Task Channels_CreateExternalMediaAsync_ShouldSendTransportDataDecodingToTheValueGiven_WhenTheRequestSetsIt()
    {
        using var handler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var http = CreateHttpClient(handler);
        IAriChannelsResource sut = new AriChannelsResource(http, DefaultOptions);

        var result = await sut.CreateExternalMediaAsync(new AriExternalMediaRequest
        {
            App = "myapp",
            ExternalHost = "media_connection1",
            Format = "slin16",
            Transport = "websocket",
            TransportData = "f(json)",
        });

        result.Id.Should().Be("ch-ext");
        handler.LastMethod.Should().Be(HttpMethod.Post);
        var transportData = QueryParameters(handler.LastRequestQuery).Where(p => p.Name == "transport_data").ToList();
        transportData.Should().ContainSingle("the parameter is spelled transport_data, and Asterisk ignores any other spelling")
            .Which.Value.Should().Be("f(json)", "the SDK sends the caller's value without parsing or rewriting it");
        // The escaped form a compiled create sent through a logging proxy, which Asterisk read as f(json) and answered
        // with a JSON MEDIA_START on every image measured.
        handler.LastRequestQuery.Should().Contain("transport_data=f%28json%29");
        handler.LastRequestHadContent.Should().BeFalse("a request with no variables has no body");
    }

    [Fact]
    public async Task Channels_CreateExternalMediaAsync_ShouldSendVariablesAsTheJsonBodyItDeclares_WhenTheRequestHoldsThem()
    {
        using var handler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var http = CreateHttpClient(handler);
        IAriChannelsResource sut = new AriChannelsResource(http, DefaultOptions);
        var variables = new Dictionary<string, string>
        {
            ["PLAIN"] = "value",
            ["Q\"UO\\TE"] = "line1\nline2 \"quoted\" \u00e9 </script> & \u0001",
        };

        await sut.CreateExternalMediaAsync(new AriExternalMediaRequest
        {
            App = "myapp",
            ExternalHost = "media_connection1",
            Format = "slin16",
            Variables = variables,
        });

        handler.LastRequestHadContent.Should().BeTrue();
        handler.LastContentType.Should().Be("application/json", "the measured create declared exactly this, with no charset");
        handler.LastContentLength.Should().Be(handler.LastRequestBody!.Length);
        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("variables");
        body.RootElement.GetProperty("variables").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!)
            .Should().BeEquivalentTo(variables);
        QueryParameters(handler.LastRequestQuery).Select(p => p.Name).Should().NotContain(
            n => n.StartsWith("variables", StringComparison.Ordinal), "the variables travel in the body only");
    }

    [Fact]
    public async Task Channels_CreateExternalMediaAsync_ShouldSendBothOptions_WhenTheRequestSetsBoth()
    {
        using var handler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var http = CreateHttpClient(handler);
        IAriChannelsResource sut = new AriChannelsResource(http, DefaultOptions);

        await sut.CreateExternalMediaAsync(new AriExternalMediaRequest
        {
            App = "myapp",
            ExternalHost = "media_connection1",
            Format = "slin16",
            TransportData = "f(json)",
            Variables = new Dictionary<string, string> { ["LABVAR"] = "1" },
        });

        QueryParameters(handler.LastRequestQuery).Should().Contain(("transport_data", "f(json)"));
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("variables").GetProperty("LABVAR").GetString().Should().Be("1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channels_CreateExternalMediaAsync_ShouldSendTheShippedRequest_WhenTheRequestSetsNeitherOption(bool emptyVariables)
    {
        using var shippedHandler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var shippedHttp = CreateHttpClient(shippedHandler);
        await new AriChannelsResource(shippedHttp, DefaultOptions).CreateExternalMediaAsync("myapp", "127.0.0.1:8000", "slin16",
            encapsulation: "rtp", transport: "udp", connectionType: "client", direction: "both",
            data: "f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0", channelId: "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");

        using var handler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var http = CreateHttpClient(handler);
        IAriChannelsResource sut = new AriChannelsResource(http, DefaultOptions);
        await sut.CreateExternalMediaAsync(new AriExternalMediaRequest
        {
            App = "myapp",
            ExternalHost = "127.0.0.1:8000",
            Format = "slin16",
            Encapsulation = "rtp",
            Transport = "udp",
            ConnectionType = "client",
            Direction = "both",
            Data = "f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0",
            ChannelId = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9",
            Variables = emptyVariables ? new Dictionary<string, string>() : null,
        });

        handler.LastMethod.Should().Be(shippedHandler.LastMethod);
        handler.LastRequestPath.Should().Be(shippedHandler.LastRequestPath);
        handler.LastRequestQuery.Should().Be(shippedHandler.LastRequestQuery);
        shippedHandler.LastRequestHadContent.Should().BeFalse();
        handler.LastRequestHadContent.Should().BeFalse("an empty Variables is no option, and sends no body");
    }

    [Fact]
    public async Task Channels_CreateExternalMediaAsync_ShouldThrowArgumentNull_WhenTheRequestIsNull()
    {
        using var handler = new FakeHttpHandler(ExternalMediaChannelJson);
        using var http = CreateHttpClient(handler);
        IAriChannelsResource sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.CreateExternalMediaAsync(request: null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("request");
        handler.LastMethod.Should().BeNull("nothing is sent");
    }

    private static readonly string ExternalMediaChannelJson = JsonSerializer.Serialize(
        new AriChannel { Id = "ch-ext", Name = "WebSocket/media_connection1" }, AriJsonContext.Default.AriChannel);

    private static List<(string Name, string Value)> QueryParameters(string? query) =>
        (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => (parts[0], Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : "")))
            .ToList();

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
    public async Task Channels_HoldAsync_ShouldSendPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.HoldAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Post);
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
    public async Task Channels_MuteAsync_ShouldSendPostWithDirection()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.MuteAsync("ch-1", "in");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestPath.Should().EndWith("channels/ch-1/mute");
        handler.LastRequestQuery.Should().Be("?direction=in");
    }

    [Fact]
    public async Task Channels_UnmuteAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.UnmuteAsync("ch-1");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestPath.Should().EndWith("channels/ch-1/mute");
        handler.LastRequestQuery.Should().Be("?direction=both");
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

    // --- ARI Domain Exceptions ---

    [Fact]
    public async Task Channels_GetAsync_ShouldThrowAriNotFoundException_WhenChannelDoesNotExist()
    {
        using var handler = new FakeHttpHandler("""{"message":"Channel not found"}""", HttpStatusCode.NotFound);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.GetAsync("nonexistent");
        await act.Should().ThrowAsync<AriNotFoundException>();
    }

    [Fact]
    public async Task Bridges_CreateAsync_ShouldThrowAriConflictException_When409()
    {
        using var handler = new FakeHttpHandler("""{"message":"Bridge already exists"}""", HttpStatusCode.Conflict);
        using var http = CreateHttpClient(handler);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var act = async () => await sut.CreateAsync("mixing", "existing-id");
        await act.Should().ThrowAsync<AriConflictException>();
    }

    [Fact]
    public async Task Channels_HangupAsync_ShouldThrowAriException_When500()
    {
        using var handler = new FakeHttpHandler("Internal Server Error", HttpStatusCode.InternalServerError);
        using var http = CreateHttpClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.HangupAsync("ch-1");
        await act.Should().ThrowAsync<AriException>()
            .Where(e => e.StatusCode == 500);
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
internal sealed class FakeHttpHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : DelegatingHandler
{
    public HttpMethod? LastMethod { get; private set; }
    public string? LastRequestUri { get; private set; }

    /// <summary>The path of the last request, still escaped as sent.</summary>
    public string? LastRequestPath { get; private set; }

    /// <summary>The query of the last request with its leading <c>?</c>, still escaped as sent.</summary>
    public string? LastRequestQuery { get; private set; }

    public bool LastRequestHadContent { get; private set; }
    public string? LastContentType { get; private set; }
    public long? LastContentLength { get; private set; }
    public byte[]? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri?.ToString();
        LastRequestPath = request.RequestUri?.AbsolutePath;
        LastRequestQuery = request.RequestUri?.Query;
        LastRequestHadContent = request.Content is not null;
        LastContentType = request.Content?.Headers.ContentType?.ToString();
        LastContentLength = request.Content?.Headers.ContentLength;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

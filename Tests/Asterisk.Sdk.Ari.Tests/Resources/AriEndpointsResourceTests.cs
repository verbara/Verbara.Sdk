using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public sealed class AriEndpointsResourceTests
{
    private static HttpClient CreateHttpClient(FakeHttpHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8088/ari/")
        };
    }

    [Fact]
    public async Task GetAsync_ShouldReturnEndpoint()
    {
        var json = JsonSerializer.Serialize(
            new AriEndpoint { Technology = "PJSIP", Resource = "2000", State = "online" },
            AriJsonContext.Default.AriEndpoint);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        var result = await sut.GetAsync("PJSIP", "2000");

        result.Technology.Should().Be("PJSIP");
        result.Resource.Should().Be("2000");
        result.State.Should().Be("online");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("endpoints/PJSIP/2000");
    }

    [Fact]
    public async Task GetAsync_ShouldEscapeParameters()
    {
        var json = JsonSerializer.Serialize(
            new AriEndpoint { Technology = "PJSIP", Resource = "ext/2000" },
            AriJsonContext.Default.AriEndpoint);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.GetAsync("PJSIP", "ext/2000");

        handler.LastRequestUri.Should().Contain("endpoints/PJSIP/ext%2F2000");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldIncludeBodyParam()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageAsync("PJSIP/3000", "PJSIP/2000", "Hello");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("body=Hello");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldOmitBody_WhenNull()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageAsync("PJSIP/3000", "PJSIP/2000");

        handler.LastRequestUri.Should().NotContain("body=");
    }

    [Fact]
    public async Task SendMessageToEndpointAsync_ShouldIncludeBodyParam()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageToEndpointAsync("PJSIP", "3000", "PJSIP/2000", "TestMsg");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("body=TestMsg");
    }

    [Fact]
    public async Task SendMessageToEndpointAsync_ShouldOmitBody_WhenNull()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriEndpointsResource(http);

        await sut.SendMessageToEndpointAsync("PJSIP", "3000", "PJSIP/2000");

        handler.LastRequestUri.Should().NotContain("body=");
    }
}

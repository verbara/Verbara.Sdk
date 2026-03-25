using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public sealed class AriApplicationsResourceTests
{
    private static HttpClient CreateHttpClient(FakeHttpHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8088/ari/")
        };
    }

    [Fact]
    public async Task ListAsync_ShouldReturnApplications()
    {
        var json = JsonSerializer.Serialize(
            new[]
            {
                new AriApplication { Name = "app1", ChannelIds = ["ch-1"] },
                new AriApplication { Name = "app2" }
            },
            AriJsonContext.Default.AriApplicationArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.ListAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("app1");
        result[0].ChannelIds.Should().Contain("ch-1");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("applications");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnApplication()
    {
        var json = JsonSerializer.Serialize(
            new AriApplication { Name = "myapp", BridgeIds = ["br-1"] },
            AriJsonContext.Default.AriApplication);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.GetAsync("myapp");

        result.Name.Should().Be("myapp");
        result.BridgeIds.Should().Contain("br-1");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("applications/myapp");
    }

    [Fact]
    public async Task GetAsync_ShouldEscapeSpecialCharsInApplicationName()
    {
        var json = JsonSerializer.Serialize(
            new AriApplication { Name = "my/app" },
            AriJsonContext.Default.AriApplication);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        await sut.GetAsync("my/app");

        handler.LastRequestUri.Should().Contain("applications/my%2Fapp");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmptyArray_WhenNoApplications()
    {
        using var handler = new FakeHttpHandler("[]");
        using var http = CreateHttpClient(handler);
        var sut = new AriApplicationsResource(http);

        var result = await sut.ListAsync();

        result.Should().BeEmpty();
    }
}

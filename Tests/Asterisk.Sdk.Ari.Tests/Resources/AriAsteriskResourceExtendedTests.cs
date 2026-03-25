using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public sealed class AriAsteriskResourceExtendedTests
{
    private static HttpClient CreateHttpClient(FakeHttpHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };

    [Fact]
    public async Task AddLogChannelAsync_ShouldPostWithConfig()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.AddLogChannelAsync("mylog", "notice,warning,error");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("asterisk/logging/mylog");
        handler.LastRequestUri.Should().Contain("configuration=notice%2Cwarning%2Cerror");
    }

    [Fact]
    public async Task DeleteLogChannelAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.DeleteLogChannelAsync("mylog");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("asterisk/logging/mylog");
    }

    [Fact]
    public async Task GetConfigAsync_ShouldReturnConfigTuples()
    {
        var json = JsonSerializer.Serialize(
            new[] { new AriConfigTuple { Attribute = "transport", Value = "udp" } },
            AriJsonContext.Default.AriConfigTupleArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.GetConfigAsync("res_pjsip", "endpoint", "6001");

        result.Should().HaveCount(1);
        result[0].Attribute.Should().Be("transport");
        handler.LastRequestUri.Should().Contain("asterisk/config/dynamic/res_pjsip/endpoint/6001");
    }

    [Fact]
    public async Task UpdateConfigAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.UpdateConfigAsync("res_pjsip", "endpoint", "6001");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("asterisk/config/dynamic/res_pjsip/endpoint/6001");
    }

    [Fact]
    public async Task DeleteConfigAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.DeleteConfigAsync("res_pjsip", "endpoint", "6001");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("asterisk/config/dynamic/res_pjsip/endpoint/6001");
    }
}

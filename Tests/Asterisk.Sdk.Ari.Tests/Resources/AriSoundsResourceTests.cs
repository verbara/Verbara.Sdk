using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public sealed class AriSoundsResourceTests
{
    private static HttpClient CreateHttpClient(FakeHttpHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8088/ari/")
        };
    }

    [Fact]
    public async Task ListAsync_ShouldReturnSounds()
    {
        var json = JsonSerializer.Serialize(
            new[]
            {
                new AriSound { Id = "hello-world", Text = "Hello World" },
                new AriSound { Id = "goodbye", Text = "Goodbye" }
            },
            AriJsonContext.Default.AriSoundArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriSoundsResource(http);

        var result = await sut.ListAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("hello-world");
        result[1].Id.Should().Be("goodbye");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("sounds");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnSound()
    {
        var json = JsonSerializer.Serialize(
            new AriSound
            {
                Id = "tt-weasels",
                Text = "Weasels have eaten my phone system",
                Formats = [new AriFormatLang { Language = "en", Format = "gsm" }]
            },
            AriJsonContext.Default.AriSound);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriSoundsResource(http);

        var result = await sut.GetAsync("tt-weasels");

        result.Id.Should().Be("tt-weasels");
        result.Text.Should().Contain("Weasels");
        result.Formats.Should().HaveCount(1);
        result.Formats[0].Language.Should().Be("en");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("sounds/tt-weasels");
    }

    [Fact]
    public async Task GetAsync_ShouldEscapeSlashInSoundId()
    {
        var json = JsonSerializer.Serialize(
            new AriSound { Id = "custom/hello" },
            AriJsonContext.Default.AriSound);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriSoundsResource(http);

        await sut.GetAsync("custom/hello");

        handler.LastRequestUri.Should().Contain("sounds/custom%2Fhello");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmptyArray_WhenNoSounds()
    {
        using var handler = new FakeHttpHandler("[]");
        using var http = CreateHttpClient(handler);
        var sut = new AriSoundsResource(http);

        var result = await sut.ListAsync();

        result.Should().BeEmpty();
    }
}

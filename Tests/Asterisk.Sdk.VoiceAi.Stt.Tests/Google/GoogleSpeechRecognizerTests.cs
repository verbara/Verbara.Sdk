using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Google;

public class GoogleSpeechRecognizerTests
{
    private const string GoogleJsonResponse = """{"results":[{"alternatives":[{"transcript":"buenos dias","confidence":0.97}]}]}""";

    [Fact]
    public async Task StreamAsync_ShouldPostJsonWithBase64Audio()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        mock.LastRequestBody.Should().Contain("content");
    }

    [Fact]
    public async Task StreamAsync_ShouldSerializeRequestWithSourceGen()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key", LanguageCode = "en-US" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequestBody.Should().Contain("en-US");
        mock.LastRequestBody.Should().Contain("LINEAR16");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeGoogleResponse()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key" }),
            new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "buenos dias" && r.Confidence == 0.97f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeApiKeyInQueryString()
    {
        var mock = new MockHttpMessageHandler(GoogleJsonResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "my-gcp-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.RequestUri!.Query.Should().Contain("my-gcp-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnEmpty_WhenNoResults()
    {
        var emptyResponse = """{"results":[]}""";
        var mock = new MockHttpMessageHandler(emptyResponse);
        var recognizer = new GoogleSpeechRecognizer(
            Options.Create(new GoogleSpeechOptions { ApiKey = "gcp-key" }),
            new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}

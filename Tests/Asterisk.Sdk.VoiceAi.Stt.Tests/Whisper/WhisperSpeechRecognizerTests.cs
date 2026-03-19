using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Whisper;

public class WhisperSpeechRecognizerTests
{
    private const string WhisperJsonResponse = """{"text":"hola mundo"}""";

    [Fact]
    public async Task StreamAsync_ShouldPostMultipartFormData()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Method.Should().Be(HttpMethod.Post);
        mock.LastRequest.Content.Should().BeOfType<MultipartFormDataContent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeResponse()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test-key" }),
            new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "hola mundo" && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendBearerAuthorizationHeader()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "my-key" }),
            new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        mock.LastRequest.Headers.Authorization.Parameter.Should().Be("my-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new WhisperSpeechRecognizer(
            Options.Create(new WhisperOptions { ApiKey = "test" }),
            new HttpClient(mock));

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}

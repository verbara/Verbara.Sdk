using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Whisper;

public class AzureWhisperSpeechRecognizerTests
{
    private const string WhisperJsonResponse = """{"text":"prueba azure"}""";

    private static AzureWhisperOptions ValidOptions => new()
    {
        ApiKey = "azure-key",
        Endpoint = new Uri("https://myresource.openai.azure.com/openai/deployments"),
        Deployment = "whisper-deployment",
        ApiVersion = "2024-02-01"
    };

    [Fact]
    public async Task StreamAsync_ShouldUseApiKeyHeader_NotBearerAuth()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.TryGetValues("api-key", out var vals).Should().BeTrue();
        vals!.Should().Contain("azure-key");
        mock.LastRequest.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeDeploymentInUrl()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("whisper-deployment");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeAzureResponse()
    {
        var mock = new MockHttpMessageHandler(WhisperJsonResponse);
        var recognizer = new AzureWhisperSpeechRecognizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.Transcript == "prueba azure" && r.IsFinal);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}

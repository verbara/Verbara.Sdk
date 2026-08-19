using System.Net;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Whisper;

/// <summary>
/// Azure OpenAI Whisper against the shared WireMock substrate (ADR-0041 D1), driven by a recorded
/// real response. This surface differs from the OpenAI one in three ways the strict matcher now
/// asserts rather than ignores: a deployment-scoped path, an <c>api-key</c> header instead of
/// bearer auth, and a mandatory <c>api-version</c> query parameter.
/// </summary>
public class AzureWhisperSpeechRecognizerTests
{
    private const string Deployment = "whisper-deployment";
    private const string ApiVersion = "2024-02-01";
    private const string ApiKey = "azure-key";
    private const string TranscriptionsPath =
        "/openai/deployments/" + Deployment + "/audio/transcriptions";

    /// <summary>The recorded capture — see its provenance sidecar for origin and terms.</summary>
    private const string RecordedResponse = "azure-openai-whisper/transcribe-short-es-co.json";

    /// <summary>
    /// Query matching is exhaustive by default, so this also asserts the client sends
    /// <c>api-version</c> and nothing else — a parameter silently dropped or added breaks the
    /// match instead of being answered anyway.
    /// </summary>
    private static HttpProviderRequest TranscriptionRequest() =>
        HttpProviderRequest.Post(TranscriptionsPath)
            .WithQuery("api-version", ApiVersion)
            .WithHeader("api-key", ApiKey);

    private static AzureWhisperOptions OptionsFor(
        HttpProviderMockServer server, string apiKey = ApiKey) =>
        new()
        {
            ApiKey = apiKey,
            Endpoint = new Uri(server.BaseAddress, "openai/deployments"),
            Deployment = Deployment,
            ApiVersion = ApiVersion
        };

    private static AzureWhisperSpeechRecognizer RecognizerFor(
        HttpProviderMockServer server, string apiKey = ApiKey) =>
        new(Options.Create(OptionsFor(server, apiKey)), server.CreateClient());

    /// <summary>
    /// The transcript the capture actually carries, read with <see cref="JsonDocument"/> rather
    /// than hard-coded — see the equivalent note in <see cref="WhisperSpeechRecognizerTests"/>.
    /// </summary>
    private static string RecordedTranscript()
    {
        using var document = JsonDocument.Parse(ProviderRecordings.Locate().ReadText(RecordedResponse));
        return document.RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldUseApiKeyHeader_NotBearerAuth()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Header("api-key").Should().Be(ApiKey);
        request.Header("Authorization").Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeDeploymentInUrl()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Path.Should().Be(TranscriptionsPath);
        request.RawQuery.Should().Contain("api-version=" + ApiVersion);
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeAzureResponse()
    {
        // Replays the vendor's real body, including any field the SDK does not model.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        var results = await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var expected = RecordedTranscript();
        expected.Should().NotBeNullOrWhiteSpace("a capture that transcribes to nothing asserts nothing");
        results.Should().ContainSingle(r => r.Transcript == expected && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldNotMatch_WhenApiKeyHeaderIsWrong()
    {
        // Strict matching (ADR-0041 D1): the previous canned handler answered every request
        // regardless of headers, so an auth regression on this surface was invisible.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server, apiKey: "wrong-key");

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenProviderReturnsError()
    {
        // A shape the canned handler could not express at all.
        await using var server = HttpProviderMockServer.Start();
        server.Stub(TranscriptionRequest(), HttpProviderResponse.Status(HttpStatusCode.TooManyRequests));
        var recognizer = RecognizerFor(server);

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence): a pre-cancelled token throws
        // OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/transport latency. No wall-clock race. Carried
        // over verbatim from the pre-WireMock suite.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz, cts.Token)
            // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
            // makes the enumerator throw on our behalf, and the assertion then cannot tell a
            // propagated throw from a silent `yield break` in the subject.
            .ToListAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();

        server.ReceivedRequests.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}

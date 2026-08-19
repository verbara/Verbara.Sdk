using System.Net;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Whisper;

/// <summary>
/// OpenAI Whisper against the shared WireMock substrate (ADR-0041 D1), driven by a recorded real
/// response. Matching is strict, so a request sent to the wrong route or without the bearer token
/// fails to match rather than receiving the canned body — which the previous
/// <c>MockHttpMessageHandler</c> could not express.
/// </summary>
public class WhisperSpeechRecognizerTests
{
    private const string TranscriptionsPath = "/v1/audio/transcriptions";
    private const string ApiKey = "openai-whisper-key";

    /// <summary>The recorded capture — see its provenance sidecar for origin and terms.</summary>
    private const string RecordedResponse = "openai-whisper/transcribe-short-es-co.json";

    private static HttpProviderRequest TranscriptionRequest() =>
        HttpProviderRequest.Post(TranscriptionsPath).WithBearerToken(ApiKey);

    private static WhisperOptions OptionsFor(HttpProviderMockServer server, string apiKey = ApiKey) =>
        new()
        {
            ApiKey = apiKey,
            Endpoint = new Uri(server.BaseAddress, TranscriptionsPath.TrimStart('/'))
        };

    private static WhisperSpeechRecognizer RecognizerFor(
        HttpProviderMockServer server, string apiKey = ApiKey) =>
        new(Options.Create(OptionsFor(server, apiKey)), server.CreateClient());

    /// <summary>
    /// The transcript the capture actually carries, read with <see cref="JsonDocument"/> rather
    /// than hard-coded. Two independent readers — this one and the SDK's source-generated parser —
    /// must agree on the vendor's bytes; hard-coding the sentence would instead assert what the
    /// capture was expected to say, and would have to be edited on every re-capture.
    /// </summary>
    private static string RecordedTranscript()
    {
        using var document = JsonDocument.Parse(ProviderRecordings.Locate().ReadText(RecordedResponse));
        return document.RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task StreamAsync_ShouldPostMultipartFormData()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Header("Content-Type").Should().StartWith("multipart/form-data");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendAudioAsWavInsideTheMultipartBody()
    {
        // The drained frames must reach the provider as a RIFF/WAVE payload. Over a canned
        // handler the body was never transported, so a malformed header could not surface.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var body = server.ReceivedRequests.Should().ContainSingle().Subject.BodyAsBytes;
        body.Should().NotBeNull();
        Encoding.ASCII.GetString(body!).Should().Contain("RIFF").And.Contain("WAVE");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeResponse()
    {
        // Replays the vendor's real body, including any field the SDK does not model: a parser
        // that threw on an unmodelled sibling passed against the hand-authored {"text":"…"}.
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
    public async Task StreamAsync_ShouldSendBearerAuthorizationHeader()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(TranscriptionRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        server.ReceivedRequests.Should().ContainSingle().Subject
            .Header("Authorization").Should().Be("Bearer " + ApiKey);
    }

    [Fact]
    public async Task StreamAsync_ShouldNotMatch_WhenBearerTokenIsWrong()
    {
        // Strict matching (ADR-0041 D1) turns a silent pass into a failure: the previous
        // canned-handler substrate answered every request regardless of headers.
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
        // OperationCanceledException at iterator entry, before any provider request is issued —
        // independent of scheduling or transport latency. Carried over verbatim from the
        // pre-WireMock suite; the substrate swap does not get to redesign it.
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

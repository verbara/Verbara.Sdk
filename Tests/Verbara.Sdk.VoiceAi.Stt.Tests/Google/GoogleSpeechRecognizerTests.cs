using System.Net;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.VoiceAi.Stt.Google;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Google;

/// <summary>
/// Google Speech-to-Text against the shared WireMock substrate (ADR-0041 D1), driven by a recorded
/// real response. This surface carries its credential in the <em>query string</em> rather than a
/// header, so strict matching is what asserts it: query matching is exhaustive, and a wrong,
/// dropped or added parameter fails to match instead of receiving the canned body — which the
/// previous <c>MockHttpMessageHandler</c> could not express.
/// </summary>
public class GoogleSpeechRecognizerTests
{
    private const string RecognizePath = "/v1/speech:recognize";

    /// <summary>Placeholder credential — never a value shaped like a real Google API key.</summary>
    private const string ApiKey = "google-stt-key";

    /// <summary>The recorded capture — see its provenance sidecar for origin, redaction and terms.</summary>
    private const string RecordedResponse = "google-stt/transcribe-short-es-co.json";

    /// <summary>
    /// Query matching is exhaustive by default, so this also asserts the client sends <c>key</c>
    /// and nothing else — the credential riding in the URL is pinned by the matcher itself.
    /// </summary>
    private static HttpProviderRequest RecognizeRequest(string apiKey = ApiKey) =>
        HttpProviderRequest.Post(RecognizePath).WithQuery("key", apiKey);

    private static GoogleSpeechRecognizer RecognizerFor(
        HttpProviderMockServer server,
        string apiKey = ApiKey,
        string? languageCode = null)
    {
        var options = new GoogleSpeechOptions { ApiKey = apiKey };
        if (languageCode is not null)
            options.LanguageCode = languageCode;

        // Only the origin is substituted (ADR-0041 D12): the route and the `key` parameter are
        // still built by the provider, so the strict matcher asserts the real request.
        return new GoogleSpeechRecognizer(
            Options.Create(options),
            server.CreateClient(),
            server.BaseAddress.ToString().TrimEnd('/'));
    }

    /// <summary>
    /// The best alternative the capture actually carries, read with <see cref="JsonDocument"/>
    /// rather than hard-coded — see the equivalent note in
    /// <see cref="Whisper.WhisperSpeechRecognizerTests"/>.
    /// </summary>
    private static (string Transcript, float Confidence) RecordedAlternative()
    {
        using var document = JsonDocument.Parse(ProviderRecordings.Locate().ReadText(RecordedResponse));
        var alternative = document.RootElement
            .GetProperty("results")[0]
            .GetProperty("alternatives")[0];

        return (alternative.GetProperty("transcript").GetString()!,
                alternative.GetProperty("confidence").GetSingle());
    }

    [Fact]
    public async Task StreamAsync_ShouldPostJsonWithBase64Audio()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Header("Content-Type").Should().StartWith("application/json");
        request.BodyAsString.Should().Contain("content");
    }

    [Fact]
    public async Task StreamAsync_ShouldSerializeRequestWithSourceGen()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server, languageCode: "en-US");

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var body = server.ReceivedRequests.Should().ContainSingle().Subject.BodyAsString;
        body.Should().Contain("en-US");
        body.Should().Contain("LINEAR16");
    }

    [Fact]
    public async Task StreamAsync_ShouldDeserializeGoogleResponse()
    {
        // Replays the vendor's real body. Note what this capture does NOT prove: its transcript is
        // lowercase, unaccented and unpunctuated as Google returned it, so it is not a UTF-8
        // round-trip witness the way the Whisper captures are.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        var results = await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var expected = RecordedAlternative();
        expected.Transcript.Should().NotBeNullOrWhiteSpace("a capture that transcribes to nothing asserts nothing");
        var result = results.Should().ContainSingle().Subject;
        result.Transcript.Should().Be(expected.Transcript);
        result.Confidence.Should().BeApproximately(expected.Confidence, 1e-6f);
        result.IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenResponseCarriesFullVendorEnvelope()
    {
        // The half a hand-authored fixture could never prove. Google's real body carries four
        // fields no DTO in VoiceAiSttJsonContext models — results[].resultEndTime,
        // results[].languageCode, totalBilledTime and requestId — and the retired literal
        // {"results":[{"alternatives":[{"transcript":…,"confidence":…}]}]} carried none of them,
        // so a parser that threw on an unmodelled sibling passed.
        //
        // The field list is asserted against the capture, not merely against this comment: shrink
        // the fixture back to the old shape and this test goes red rather than quietly weakening.
        //
        // languageCode is worth naming: the request asks for es-CO and Google answered "es-us".
        // The SDK models no language on SpeechRecognitionResult, so that divergence is silently
        // dropped today — visible here, and invisible in every fixture written before this one.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        using var capture = JsonDocument.Parse(ProviderRecordings.Locate().ReadText(RecordedResponse));
        var root = capture.RootElement;
        var firstResult = root.GetProperty("results")[0];
        firstResult.TryGetProperty("resultEndTime", out _).Should().BeTrue();
        firstResult.TryGetProperty("languageCode", out var language).Should().BeTrue();
        language.GetString().Should().Be("es-us");
        root.TryGetProperty("totalBilledTime", out _).Should().BeTrue();
        root.TryGetProperty("requestId", out _).Should().BeTrue();

        var results = await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var expected = RecordedAlternative().Transcript;
        results.Should().ContainSingle(r => r.Transcript == expected && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldIncludeApiKeyInQueryString()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Path.Should().Be(RecognizePath);
        request.RawQuery.Should().Contain("key=" + ApiKey);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnEmpty_WhenNoResults()
    {
        // Hand-authored on purpose: an empty-result response is a shape no capture in this tree
        // holds, and D4 governs the fixture of record rather than every stub in the suite.
        await using var server = HttpProviderMockServer.Start();
        server.Stub(RecognizeRequest(), HttpProviderResponse.Json("""{"results":[]}"""));
        var recognizer = RecognizerFor(server);

        var results = await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotMatch_WhenApiKeyInQueryStringIsWrong()
    {
        // Strict matching (ADR-0041 D1) turns a silent pass into a failure: the previous
        // canned-handler substrate answered every request regardless of the URL it was sent to,
        // so an auth regression on the one surface that authenticates via the query string was
        // structurally invisible.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
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
        server.Stub(RecognizeRequest(), HttpProviderResponse.Status(HttpStatusCode.TooManyRequests));
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
        // issued — independent of scheduling/mock latency. No wall-clock race. Carried over
        // verbatim from the pre-WireMock suite; the substrate swap does not get to redesign it.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedJson(RecognizeRequest(), RecordedResponse);
        var recognizer = RecognizerFor(server);

        var act = async () => await recognizer
            .StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        server.ReceivedRequests.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }
}

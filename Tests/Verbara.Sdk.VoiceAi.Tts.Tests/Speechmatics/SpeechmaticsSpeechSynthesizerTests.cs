using System.Net;
using System.Text;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.VoiceAi.Tts.Speechmatics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Speechmatics;

/// <summary>
/// Speechmatics TTS against the shared WireMock substrate (ADR-0041 D1), driven by a recorded real
/// response. Matching is strict, so a request sent to the wrong route or without the vendor's bearer
/// token fails to match rather than receiving the canned body — which the retired
/// <c>SpeechmaticsFakeServer</c> could only approximate, and which the fake before it could not
/// express at all.
/// </summary>
/// <remarks>
/// <para>
/// The capture beside this file is what unblocked the migration. Until 2026-08-15 the client posted
/// to <c>/generate</c> with the voice as a body field and the live API answered <c>404</c>, so no
/// fixture could pin both the client's request and a working response at once: strict matching makes
/// that contradiction a failure instead of papering over it. The route fix landed first
/// (<c>Sdk/ADR-0048</c>), and the capture on 2026-08-17 settled the question that fix deliberately
/// left open — whether <c>/generate/{voice}</c> also accepts the <c>language</c> and
/// <c>sample_rate</c> body fields the client still sends. It does: the recorded response is the
/// answer to a request carrying all three.
/// </para>
/// </remarks>
public class SpeechmaticsSpeechSynthesizerTests
{
    private const string ApiKey = "test-key";
    private const string Voice = SpeechmaticsVoices.Jack;

    /// <summary>The route the vendor actually serves — the voice is a path segment, not a body field.</summary>
    private const string SynthesisPath = "/generate/" + Voice;

    /// <summary>The recorded capture — see its provenance sidecar for origin and terms.</summary>
    private const string RecordedResponse = "speechmatics-tts/synthesize-short-en-us.wav";

    /// <summary>The media type the vendor declared on that capture.</summary>
    private const string RecordedMediaType = "audio/wav";

    /// <summary>Length of the capture, as its sidecar records it.</summary>
    private const int RecordedLength = 73772;

    /// <summary>The synthesizer's own read buffer — <c>SpeechmaticsSpeechSynthesizer.ChunkSize</c>.</summary>
    private const int ChunkSize = 8192;

    private static SpeechmaticsOptions ValidOptions => new()
    {
        ApiKey = ApiKey,
        Voice = Voice,
        Language = "en",
        SampleRate = 16000,
    };

    private static HttpProviderRequest SynthesisRequest(string path = SynthesisPath) =>
        HttpProviderRequest.Post(path).WithBearerToken(ApiKey);

    private static SpeechmaticsSpeechSynthesizer SynthesizerFor(
        HttpProviderMockServer server,
        Action<SpeechmaticsOptions>? configure = null)
    {
        var opts = ValidOptions;
        configure?.Invoke(opts);
        return new SpeechmaticsSpeechSynthesizer(
            Options.Create(opts),
            server.CreateClient(),
            server.BaseAddress.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldPostJson_WithTextLanguageAndSampleRate()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server, o => o.Language = "es");

        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.BodyAsString.Should().Contain("\"text\":\"hola mundo\"");
        request.BodyAsString.Should().Contain("\"language\":\"es\"");
        // AudioFormat.Slin16Mono8kHz → 8000 Hz; the option's 16000 is the fallback, not the winner.
        request.BodyAsString.Should().Contain("\"sample_rate\":8000");
        request.Header("Content-Type").Should().StartWith("application/json");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendBearerToken_WhenCredentialIsConfigured()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        server.ReceivedRequests.Should().ContainSingle().Subject
            .Header("Authorization").Should().Be("Bearer " + ApiKey);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSelectVoiceByPathSegment_NotByBodyField()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Matching is the assertion: the stub declares POST /generate/jack and nothing else, so a
        // request that put the voice anywhere but the path would land in UnmatchedRequests.
        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Path.Should().Be(SynthesisPath);
        server.UnmatchedRequests.Should().BeEmpty();

        // And it is NOT also sent in the body: which one wins when they disagree was never measured.
        request.BodyAsString.Should().NotContain("\"voice\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotMatch_WhenTheOnlyRouteOfferedIsBareGenerate()
    {
        // Regression guard for the shipped defect, inverted so it can actually fail. Every request
        // this client made until 2026-08-15 went to /generate with the voice in the body, which the
        // live API answers 404 — and the fake of the day inspected no path, so three green tests
        // certified a route that had never once worked. Here the substrate offers ONLY that broken
        // route: a client that reverts to it would match and this test would go red.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest("/generate"), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server);

        var act = async () => await synth
            .SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle().Subject.Path.Should().Be(SynthesisPath);
    }

    /// <summary>
    /// A voice carrying URI-reserved characters must reach the vendor intact rather than being dropped
    /// or double-escaped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>State the fidelity this substrate does not have, and state it at full width.</strong>
    /// The retired <c>HttpListener</c> fake read <c>Uri.AbsolutePath</c>, which keeps <c>%2F</c>
    /// escaped, so it could prove the escaped slash stayed inside one path segment. This substrate
    /// cannot, and the loss is larger than one reserved character: measured 2026-08-17, the request
    /// target is decoded <em>twice</em> before the matcher sees it, so the escaped
    /// <c>/generate/a%20b%2Fc</c>, the unescaped <c>/generate/a%20b/c</c> and the double-escaped
    /// <c>/generate/a%2520b%252Fc</c> all arrive as <c>/generate/a b/c</c> — and all three match.
    /// Escaping is not observable here at any level: not the reserved slash, and not how many times it
    /// was encoded. That property is a client property (<c>Uri.EscapeDataString</c> in
    /// <c>SpeechmaticsSpeechSynthesizer</c>) and a route-level vendor question, and it belongs with the
    /// wire-conformance work rather than being faked green.
    /// </para>
    /// <para>
    /// What the assertion below still pins is therefore narrower than its first draft claimed: the
    /// voice's characters reach the route intact. A client that truncated the voice at the reserved
    /// character, dropped part of it or substituted something else would produce a different decoded
    /// path and fail to match. A client that stopped escaping would <em>not</em> — that arm was
    /// asserted here until the measurement above refuted it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldCarryTheVoiceIntact_WhenItContainsUriReservedCharacters()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(
            SynthesisRequest("/generate/a b/c"), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server, o => o.Voice = "a b/c");

        var act = async () => await synth
            .SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
        server.UnmatchedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldRecordedAudioIntact_WhenChunked()
    {
        // The point of the recording: real WAV bytes from the vendor traverse the read loop, which
        // 10 000 bytes of a counting pattern could not exercise. Frame boundaries are deliberately
        // not asserted — the client yields whatever Stream.ReadAsync returned, and over a chunked
        // response that does not align to the buffer. What IS asserted is that the capture's length
        // is not buffer-aligned, so at least one partial chunk must reach the consumer.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var expected = ProviderRecordings.Locate().ReadBytes(RecordedResponse);
        var synth = SynthesizerFor(server);

        var chunks = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        (expected.Length % ChunkSize).Should().NotBe(0, "the capture must not be chunk-aligned");
        chunks.Should().HaveCountGreaterThan(1, "the response must actually be chunked");
        chunks.Should().OnlyContain(c => c.Length > 0 && c.Length <= ChunkSize);
        chunks.Should().Contain(c => c.Length != ChunkSize, "a partial chunk must reach the consumer");
        chunks.SelectMany(c => c.ToArray()).Should().Equal(expected,
            "chunking must not alter a single byte of the vendor's audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotMatch_WhenBearerTokenIsWrong()
    {
        // Strict matching (ADR-0041 D1) turns a silent pass into a failure: the retired fake read the
        // Authorization header but answered 200 whatever it said.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server, o => o.ApiKey = "wrong-key");

        var act = async () => await synth
            .SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldThrow_WhenProviderReturnsError()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(SynthesisRequest(), HttpProviderResponse.Status(HttpStatusCode.Unauthorized));
        var synth = SynthesizerFor(server);

        var act = async () => await synth
            .SynthesizeAsync("fail", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// <c>ADR-0050</c> E5 on the HTTP transport: a request that succeeds but carries no audio must
    /// reach the caller as <see cref="SpeechProviderEmptyResultException"/>, not as a stream that
    /// simply ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this arm is and is not.</strong> It closes a gap in a contract this package
    /// publishes — the promise is declared in <c>SpeechSynthesizer.SynthesizeAsync</c>'s XML docs and
    /// was honoured by the other five synthesizers and not this one. It is <em>not</em> a
    /// reproduction of a failure the vendor is currently observed to produce: probed on 2026-08-18,
    /// the live route never answered with an empty body — the smallest response seen was a 44-byte
    /// RIFF header plus 7 680 bytes of data. The stub below is therefore a contract fixture, and
    /// saying so is the point; a test that implied "the vendor does this" would be claiming a
    /// measurement nobody took.
    /// </para>
    /// <para>
    /// Note what is deliberately not wrapped: a non-2xx status keeps raising
    /// <see cref="HttpRequestException"/> (the test above). It is already typed, it already carries
    /// the vendor's status, and it is not one of the silent doors E5 closes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenTheResponseSucceedsWithNoAudio()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(SynthesisRequest(), HttpProviderResponse.Bytes([], RecordedMediaType));
        var synth = SynthesizerFor(server);

        var act = async () => await synth
            .SynthesizeAsync("silence", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "the request succeeded — the response merely carried no audio");
        empty.Provider.Should().Be("Speechmatics");
        server.UnmatchedRequests.Should().BeEmpty("the request itself was well-formed");
    }

    /// <summary>
    /// The other half of E5: <c>text</c> that carries no speech yields nothing <em>without asking the
    /// provider for anything</em>. Asserted through the substrate seeing no request at all — matched
    /// or otherwise — which is also the negative control: delete the guard and this goes red, because
    /// a request would appear.
    /// </summary>
    /// <remarks>
    /// This half does reproduce a live defect. Probed on 2026-08-18, <c>POST /generate/{voice}</c>
    /// answers whitespace text <c>200 audio/wav</c> with 7 724 bytes carrying 0.24 s of audible
    /// audio — so before the guard, a caller the contract promised silence was billed for a request
    /// and handed speech. The stub is armed with the normal recorded response precisely so the test
    /// cannot pass by accident: if a request were issued it would match and yield audio.
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldYieldNothingWithoutRequesting_WhenTextIsWhitespace()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, RecordedMediaType);
        var synth = SynthesizerFor(server);

        var chunks = await synth.SynthesizeAsync("   ", AudioFormat.Slin16Mono8kHz).ToListAsync();

        chunks.Should().BeEmpty();
        server.ReceivedRequests.Should().BeEmpty("no request should have been issued at all");
        server.UnmatchedRequests.Should().BeEmpty("not an unmatched one either");
    }

    [Fact]
    public void RecordedCapture_ShouldBeTheWavItsSidecarDescribes_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The stub is only as good as what is on disk: re-save the capture
        // or swap it for something smaller and the suite would keep passing while quietly testing
        // less. Both numbers below are the sidecar's.
        var audio = ProviderRecordings.Locate().ReadBytes(RecordedResponse);

        audio.Should().HaveCount(RecordedLength, "the sidecar records this exact length");
        (audio.Length % ChunkSize).Should().NotBe(0);
        Encoding.ASCII.GetString(audio, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(audio, 8, 4).Should().Be("WAVE",
            "the vendor declared audio/wav and the bytes must actually be one");
    }
}

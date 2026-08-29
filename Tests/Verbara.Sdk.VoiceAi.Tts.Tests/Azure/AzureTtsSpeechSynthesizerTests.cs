using System.Net;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.VoiceAi.Tts.Azure;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Azure;

/// <summary>
/// Azure TTS against the shared WireMock substrate (ADR-0041 D1), driven by a recorded real
/// response. Matching is strict, so a request sent to the wrong route or without the provider's
/// API-key header fails to match rather than receiving the canned body — which the previous
/// <c>MockHttpMessageHandler</c> could not express.
/// </summary>
public class AzureTtsSpeechSynthesizerTests
{
    private const string SynthesisPath = "/cognitiveservices/v1";
    private const string ApiKey = "azure-tts-key";

    /// <summary>The recorded capture — see its provenance sidecar for origin and terms.</summary>
    private const string RecordedResponse = "azure-tts/synthesize-short-es-co.raw";

    private static AzureTtsOptions ValidOptions => new()
    {
        ApiKey = ApiKey,
        Region = "eastus",
        VoiceName = "es-CO-SalomeNeural",
        OutputFormat = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm
    };

    private static HttpProviderRequest SynthesisRequest() =>
        HttpProviderRequest.Post(SynthesisPath).WithHeader("Ocp-Apim-Subscription-Key", ApiKey);

    private static AzureTtsSpeechSynthesizer SynthesizerFor(
        HttpProviderMockServer server, int chunkSize = 4096) =>
        new(Options.Create(ValidOptions),
            server.CreateClient(),
            chunkSize,
            server.BaseAddress.ToString().TrimEnd('/'));

    [Fact]
    public async Task SynthesizeAsync_ShouldPostSsmlXml()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var request = server.ReceivedRequests.Should().ContainSingle().Subject;
        request.Header("Content-Type").Should().StartWith("application/ssml+xml");
    }

    /// <summary>
    /// The precedence rule on the one surface that satisfies it without stating it. This
    /// synthesizer has neither an entry guard nor a blank-text shortcut, so the cancellation
    /// surfaces from <c>HttpClient.SendAsync(…, ct)</c> as <see cref="TaskCanceledException"/> —
    /// which derives from <see cref="OperationCanceledException"/>, so the contract holds.
    /// </summary>
    /// <remarks>
    /// Do not read this test's green as evidence that the file states the ordering rule: there is
    /// no ordering here to state. If a blank-text shortcut is ever added to this synthesizer, it
    /// MUST go below a token check, and this test is what will fail if it does not.
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var synth = SynthesizerFor(server);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await synth
            .SynthesizeAsync("   ", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        server.ReceivedRequests.Should().BeEmpty("the cancellation is observed before any request is issued");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldEscapeXmlInText()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("<script>alert('xss')</script>", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var body = server.ReceivedRequests.Should().ContainSingle().Subject.BodyAsString;
        body.Should().NotContain("<script>");
        body.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldUseApiKeyHeader()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var synth = SynthesizerFor(server);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        server.ReceivedRequests.Should().ContainSingle().Subject
            .Header("Ocp-Apim-Subscription-Key").Should().Be(ApiKey);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldRecordedAudioIntact_WhenChunked()
    {
        // The point of the recording: real codec bytes traverse the frame-chunking path, which
        // 320 zero bytes could not exercise.
        //
        // Note what is NOT asserted. Frames are not all `chunkSize` long: the provider yields
        // whatever `Stream.ReadAsync` returns, and over a real chunked response that does not
        // align to the buffer. An earlier version of this test asserted the final frame was
        // exactly `length % 320` and failed — it encoded an assumption about read alignment that
        // neither the client nor the transport guarantees. What IS guaranteed is below: because
        // the capture's length is deliberately not a multiple of the chunk size, at least one
        // frame must be partial, which the previous new byte[320] / new byte[640] fixtures —
        // exact multiples — could never produce.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var expected = ProviderRecordings.Locate().ReadBytes(RecordedResponse);
        var synth = SynthesizerFor(server, chunkSize: 320);

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        (expected.Length % 320).Should().NotBe(0, "the capture must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the response must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= 320);
        frames.Should().Contain(f => f.Length != 320, "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "chunking must not alter a single byte of the vendor's audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotMatch_WhenApiKeyHeaderIsWrong()
    {
        // Strict matching (ADR-0041 D1) turns a silent pass into a failure: the previous
        // canned-handler substrate answered every request regardless of headers.
        await using var server = HttpProviderMockServer.Start();
        server.StubRecordedBytes(SynthesisRequest(), RecordedResponse, "audio/basic");
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(new AzureTtsOptions
            {
                ApiKey = "wrong-key",
                Region = "eastus",
                VoiceName = "es-CO-SalomeNeural",
                OutputFormat = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm
            }),
            server.CreateClient(),
            chunkSize: 4096,
            server.BaseAddress.ToString().TrimEnd('/'));

        var act = async () =>
            await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        server.UnmatchedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldThrow_WhenProviderReturnsError()
    {
        // A shape the canned handler could not express at all.
        await using var server = HttpProviderMockServer.Start();
        server.Stub(SynthesisRequest(), HttpProviderResponse.Status(HttpStatusCode.TooManyRequests));
        var synth = SynthesizerFor(server);

        var act = async () =>
            await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}

using System.Net;
using FluentAssertions;
using Verbara.Sdk.TestInfrastructure.Http;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// Tests for the shared WireMock substrate itself (ADR-0041). It lives in this suite because this
/// is the first suite that consumes it; nothing here is STT-specific.
/// </summary>
public class HttpProviderMockServerTests
{
    private const string TranscriptionPath = "/v1/audio/transcriptions";
    private const string TranscriptionJson = """{"text":"hola mundo"}""";

    private static HttpProviderRequest AuthenticatedTranscription() =>
        HttpProviderRequest.Post(TranscriptionPath).WithBearerToken("test-key");

    private static HttpRequestMessage Authenticated(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-key");
        request.Content = new StringContent("audio");
        return request;
    }

    [Fact]
    public async Task BaseAddress_ShouldNameTheIPv4LoopbackLiteral_WhenServerStarts()
    {
        await using var server = HttpProviderMockServer.Start();

        // ADR-0044: never 'localhost' — it resolves ::1 first and cross-wires IPv4-only fakes.
        server.BaseAddress.Host.Should().Be("127.0.0.1");
        server.BaseAddress.Port.Should().Be(server.Port);
    }

    [Fact]
    public async Task Stub_ShouldServeTheStubbedBody_WhenMethodPathAndHeadersMatch()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(TranscriptionJson);
        server.ReceivedRequests.Should().ContainSingle()
            .Which.Header("Authorization").Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task Stub_ShouldNotMatch_WhenRequiredAuthHeaderIsMissing()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.PostAsync(TranscriptionPath, new StringContent("audio"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        server.UnmatchedRequests.Should().ContainSingle().Which.Path.Should().Be(TranscriptionPath);
    }

    [Fact]
    public async Task Stub_ShouldNotMatch_WhenPathDiffers()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.SendAsync(Authenticated(HttpMethod.Post, "/v1/audio/translations"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stub_ShouldNotMatch_WhenMethodDiffers()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.SendAsync(Authenticated(HttpMethod.Put, TranscriptionPath));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stub_ShouldNotMatch_WhenDeclaredQueryParameterHasAnotherValue()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(
            HttpProviderRequest.Post("/v1/speech:recognize").WithQuery("key", "placeholder-api-key"),
            HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.PostAsync("/v1/speech:recognize?key=other", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stub_ShouldNotMatch_WhenAnUndeclaredQueryParameterIsPresent()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath + "?debug=1"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stub_ShouldMatch_WhenUndeclaredQueryParametersAreExplicitlyAllowed()
    {
        await using var server = HttpProviderMockServer.Start();
        server.Stub(
            AuthenticatedTranscription().AllowingUndeclaredQueryParameters(),
            HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath + "?debug=1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StubSequence_ShouldReturnEachResponseInOrder_AndRepeatTheLast()
    {
        await using var server = HttpProviderMockServer.Start();
        server.StubSequence(
            AuthenticatedTranscription(),
            HttpProviderResponse.Status(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""),
            HttpProviderResponse.Status(HttpStatusCode.ServiceUnavailable),
            HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var call = 0; call < 4; call++)
        {
            var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath));
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK,
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task StubRecordedJson_ShouldServeTheCapture_WhenTheRecordingExists()
    {
        var folder = CreateTemporaryRecordingsFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "whisper"));
            await File.WriteAllTextAsync(Path.Combine(folder, "whisper", "transcription.json"), TranscriptionJson);

            await using var server = HttpProviderMockServer.Start(ProviderRecordings.At(folder));
            server.StubRecordedJson(AuthenticatedTranscription(), "whisper/transcription.json");
            using var client = server.CreateClient();

            var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath));

            (await response.Content.ReadAsStringAsync()).Should().Be(TranscriptionJson);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task StubRecordedBytes_ShouldServeTheCaptureVerbatim_WhenTheRecordingExists()
    {
        // Bytes above 0x7F are the ones a text-shaped substrate silently mangles.
        byte[] audio = [0x00, 0x7F, 0x80, 0xFF, 0x10, 0xC3];
        var folder = CreateTemporaryRecordingsFolder();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "azure-tts.wav"), audio);

            await using var server = HttpProviderMockServer.Start(ProviderRecordings.At(folder));
            server.StubRecordedBytes(AuthenticatedTranscription(), "azure-tts.wav", "audio/wav");
            using var client = server.CreateClient();

            var response = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath));

            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(audio);
            response.Content.Headers.ContentType!.MediaType.Should().Be("audio/wav");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ChunkedBytes_ShouldStreamEveryByte_WhenTheBodyIsSplitAcrossChunks()
    {
        byte[][] chunks = [[0x00, 0x7F, 0x80, 0xFF], [0x10, 0xC3, 0xA9, 0x01]];
        await using var server = HttpProviderMockServer.Start();
        server.Stub(
            AuthenticatedTranscription(),
            HttpProviderResponse.ChunkedBytes(chunks, "audio/raw", TimeSpan.FromMilliseconds(10)));
        using var client = server.CreateClient();

        var response = await client.SendAsync(
            Authenticated(HttpMethod.Post, TranscriptionPath),
            HttpCompletionOption.ResponseHeadersRead);

        response.Content.Headers.ContentLength.Should().BeNull("a streamed body has no known length");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal([.. chunks[0], .. chunks[1]]);
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopServingRequests_WhenTheServerIsDisposed()
    {
        // The invariant dispose actually owns is "the server stops serving" — NOT "this exact port
        // number is rebindable right now". An earlier version of this test asserted the latter by
        // rebinding the freed port, and failed ~5% of the time under multi-process load. Measured
        // cause, before changing anything: across 720 start/dispose/rebind cycles in 6 concurrent
        // processes, every refused rebind became bindable at the first re-check 25 ms later
        // (never-recovered = 0), and one refusal was a provable cross-process steal. So there is no
        // port leak — Kestrel's dispose returns before the kernel has finished releasing the
        // listening socket, and a freed port can also legitimately be taken by another process.
        // Neither is something this fixture can guarantee, so neither belongs in an assertion; the
        // acquire side already handles the same latency via PortAllocationAttempts.
        var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        using (var live = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath)))
            live.StatusCode.Should().Be(HttpStatusCode.OK, "the server serves before dispose");

        await server.DisposeAsync();

        var afterDispose = async () =>
            await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath));

        await afterDispose.Should().ThrowAsync<HttpRequestException>(
            "dispose must stop the server answering on its address");
    }

    private static string CreateTemporaryRecordingsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "verbara-recordings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

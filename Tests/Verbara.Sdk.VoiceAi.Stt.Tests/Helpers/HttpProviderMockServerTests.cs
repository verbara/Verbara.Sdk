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
    public async Task DisposeAsync_ShouldRejectFurtherUse_WhenTheServerIsDisposed()
    {
        // This assertion is deliberately NOT a network probe, and the two earlier versions that
        // were both failed in CI while passing in isolation.
        //
        //   v1 asserted the freed port was immediately rebindable. Measured over 720 cycles in 6
        //      concurrent processes: ~5% of rebinds were refused, every one became bindable 25 ms
        //      later (never-recovered = 0), and one refusal was a provable cross-process steal.
        //      Kestrel's dispose returns before the kernel finishes releasing the socket.
        //   v2 asserted a post-dispose request threw. Reusing the pre-dispose client hit a drained
        //      keep-alive connection (1 in 900). Switching to a fresh client still failed, because
        //      HttpClient does NOT throw on 4xx: a sibling fixture in this same assembly takes the
        //      just-freed port and answers 404, which is a perfectly valid response.
        //
        // The port is a shared OS resource, so nothing reached through it is deterministic — the
        // resource-identity lesson of ADR-0044 again. What dispose genuinely owns is this object's
        // own state, so that is what gets asserted.
        var server = HttpProviderMockServer.Start();
        server.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json(TranscriptionJson));
        using var client = server.CreateClient();

        using (var live = await client.SendAsync(Authenticated(HttpMethod.Post, TranscriptionPath)))
            live.StatusCode.Should().Be(HttpStatusCode.OK, "the server serves before dispose");

        await server.DisposeAsync();

        server.Invoking(s => s.CreateClient()).Should().Throw<ObjectDisposedException>();
        server.Invoking(s => s.Stub(AuthenticatedTranscription(), HttpProviderResponse.Json("{}")))
            .Should().Throw<ObjectDisposedException>();

        var disposeAgain = async () => await server.DisposeAsync();
        await disposeAgain.Should().NotThrowAsync("dispose is idempotent");
    }

    private static string CreateTemporaryRecordingsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "verbara-recordings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Cartesia;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>CartesiaFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from recorded
/// frames (D4), not from a different server.
/// </summary>
public class CartesiaSpeechRecognizerTests : IAsyncDisposable
{
    private readonly CartesiaFakeServer _server;

    public CartesiaSpeechRecognizerTests()
    {
        _server = new CartesiaFakeServer();
        _server.Start();
    }

    private CartesiaSpeechRecognizer BuildRecognizer(Action<CartesiaOptions>? configure = null)
    {
        var opts = new CartesiaOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        return new CartesiaSpeechRecognizer(Options.Create(opts), fakeServerPort: _server.Port);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendStartConfig_WhenConnectionOpens()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var init = _server.ReceivedJsonMessages[0];
        init.Should().Contain("\"type\":\"start\"");
        init.Should().Contain("\"model\":\"ink-whisper\"");
        init.Should().Contain("\"sample_rate\":8000");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimTranscript_WhenIsFinalFalse()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola", 0.7f, isFinal: false));
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola mundo", 0.95f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be("hola");
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be("hola mundo");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTranscript_WithConfidence()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("prueba", 0.88f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.IsFinal && Math.Abs(r.Confidence - 0.88f) < 0.001f);
    }

    [Fact]
    public async Task StreamAsync_ShouldComplete_WhenServerAborts()
    {
        _server.AbortAfterSend = true;
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence): a pre-cancelled token throws
        // OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/mock latency. No wall-clock race against the
        // fake server (see openspec/changes/archive/2026-07-05-stt-cancellation-test-fence).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(
                    SttFrameGenerators.EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedFrameCount.Should().Be(0);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}

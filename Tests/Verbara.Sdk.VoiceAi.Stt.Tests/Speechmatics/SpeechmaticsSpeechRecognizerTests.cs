using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>SpeechmaticsFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from
/// recorded frames (D4), not from a different server. The Speechmatics <em>TTS</em> suite is a
/// separate, HTTP-transport surface and does migrate (§4.5).
/// </summary>
public class SpeechmaticsSpeechRecognizerTests : IAsyncDisposable
{
    private readonly SpeechmaticsFakeServer _server;

    public SpeechmaticsSpeechRecognizerTests()
    {
        _server = new SpeechmaticsFakeServer();
        _server.Start();
    }

    private SpeechmaticsSpeechRecognizer BuildRecognizer(Action<SpeechmaticsOptions>? configure = null)
    {
        var opts = new SpeechmaticsOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        return new SpeechmaticsSpeechRecognizer(Options.Create(opts), fakeServerPort: _server.Port);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendStartRecognition_WithAudioAndTranscriptionConfig()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o =>
        {
            o.Language = "es";
            o.OperatingPoint = "enhanced";
            o.EnablePartials = true;
            o.MaxDelaySeconds = 2;
            o.SampleRate = 16000;
        });

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedStartRecognitionJson.Should().NotBeNullOrEmpty();
        var json = _server.ReceivedStartRecognitionJson!;
        json.Should().Contain("\"message\":\"StartRecognition\"");
        json.Should().Contain("\"encoding\":\"pcm_s16le\"");
        json.Should().Contain("\"language\":\"es\"");
        json.Should().Contain("\"operating_point\":\"enhanced\"");
        json.Should().Contain("\"enable_partials\":true");
        // URL should carry the jwt query parameter.
        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().Contain("jwt=test-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldPartialTranscript_WhenAddPartialTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hola", 0.80f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola");
        results[0].IsFinal.Should().BeFalse();
        results[0].Confidence.Should().BeApproximately(0.80f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTranscript_WhenAddTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.99f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola mundo");
        results[0].IsFinal.Should().BeTrue();
        results[0].Confidence.Should().BeApproximately(0.99f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreLifecycleMessages_WhenEndOfTranscript()
    {
        // Server sends RecognitionStarted automatically. Add only EndOfTranscript — no
        // AddPartialTranscript / AddTranscript — so we expect zero yielded results.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildEndOfTranscriptJson());

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
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

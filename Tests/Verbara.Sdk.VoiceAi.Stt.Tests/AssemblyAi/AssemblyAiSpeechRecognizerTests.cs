using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.AssemblyAi;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>AssemblyAiFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from recorded
/// frames (D4), not from a different server.
/// </summary>
public class AssemblyAiSpeechRecognizerTests : IAsyncDisposable
{
    private readonly AssemblyAiFakeServer _server;

    public AssemblyAiSpeechRecognizerTests()
    {
        _server = new AssemblyAiFakeServer();
        _server.Start();
    }

    private AssemblyAiSpeechRecognizer BuildRecognizer(Action<AssemblyAiOptions>? configure = null)
    {
        var opts = new AssemblyAiOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        return new AssemblyAiSpeechRecognizer(Options.Create(opts), fakeServerPort: _server.Port);
    }

    [Fact]
    public async Task StreamAsync_ShouldConnect_WithCorrectQueryString_WhenStarted()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o =>
        {
            o.SampleRate = 16000;
            o.FormatTurns = 1;
            o.EndOfTurnConfidenceThreshold = 800;
        });

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().Contain("sample_rate=16000");
        _server.ReceivedRequestUri.Should().Contain("format_turns=1");
        _server.ReceivedRequestUri.Should().Contain("end_of_turn_confidence_threshold=800");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTurn_WhenEndOfTurnTrue()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTurnJson("hola mundo", endOfTurn: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola mundo");
        results[0].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimTurn_WhenEndOfTurnFalse()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTurnJson("hola", endOfTurn: false));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola");
        results[0].IsFinal.Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreBeginAndTermination_NotYieldResult()
    {
        // Server automatically sends Begin on connect. Add only a Termination after — no Turn.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(AssemblyAiFakeServer.BuildTerminationJson());

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

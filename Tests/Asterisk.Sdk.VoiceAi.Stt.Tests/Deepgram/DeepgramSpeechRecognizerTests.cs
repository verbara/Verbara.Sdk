using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Deepgram;

public class DeepgramSpeechRecognizerTests : IAsyncDisposable
{
    private readonly DeepgramFakeServer _server;

    public DeepgramSpeechRecognizerTests()
    {
        _server = new DeepgramFakeServer();
        _server.Start();
    }

    private DeepgramSpeechRecognizer BuildRecognizer(Action<DeepgramOptions>? configure = null)
    {
        var opts = new DeepgramOptions { ApiKey = "test-key" };
        configure?.Invoke(opts);
        return new DeepgramSpeechRecognizer(
            Options.Create(opts),
            fakeServerPort: _server.Port);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldInterimResult()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola", 0.8f, isFinal: false));
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("hola mundo", 0.99f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be("hola");
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be("hola mundo");
    }

    [Fact]
    public async Task StreamAsync_ShouldSendAudioFrames()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedFrameCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalResult_WithCorrectConfidence()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(DeepgramFakeServer.BuildResultJson("prueba", 0.95f, isFinal: true));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle(r => r.IsFinal && r.Confidence == 0.95f);
    }

    [Fact]
    public async Task StreamAsync_ShouldComplete_WhenServerClosesConnection()
    {
        var recognizer = BuildRecognizer();
        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var recognizer = BuildRecognizer();

        // Cancel only after the fake server has confirmed receipt of at least
        // one frame, guaranteeing the streaming loop is mid-flight. The prior
        // wall-clock-timer approach (CancelAfter(100ms)) was flaky on slow
        // GitHub Actions runners where setup + first-frame send sometimes
        // exceeded the timeout window — see issue #32.
        var cancelTrigger = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_server.ReceivedFrameCount == 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        "Fake Deepgram server did not receive any frame within 5s; recognizer never started streaming.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            cts.Cancel();
        });

        var act = async () =>
            await recognizer.StreamAsync(EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        await cancelTrigger; // surface any helper-side timeout
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ThreeFrames()
    {
        for (int i = 0; i < 3; i++) yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EndlessFrames(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return new byte[320];
            await Task.Delay(10, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}

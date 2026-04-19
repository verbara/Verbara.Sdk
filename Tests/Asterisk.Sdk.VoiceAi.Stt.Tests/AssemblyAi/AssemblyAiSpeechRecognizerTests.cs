using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.AssemblyAi;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

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

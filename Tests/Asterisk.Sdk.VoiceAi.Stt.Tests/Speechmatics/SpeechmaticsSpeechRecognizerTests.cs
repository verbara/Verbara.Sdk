using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Speechmatics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Speechmatics;

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

using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

public class SilenceTurnDetectorTests
{
    private static readonly VoiceAiPipelineOptions DefaultOptions = new()
    {
        SilenceThresholdDb = -40.0,
        EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
        BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        MaxUtteranceDuration = TimeSpan.FromMilliseconds(100),
    };

    private static SilenceTurnDetector CreateDetector(VoiceAiPipelineOptions? options = null)
        => new(Options.Create(options ?? DefaultOptions));

    private static short[] VoiceSamples()
    {
        var samples = new short[160];
        Array.Fill(samples, (short)5000);
        return samples;
    }

    private static short[] SilenceSamples() => new short[160];

    [Fact]
    public void Analyze_ShouldReturnSpeechStarted_WhenFirstVoiceFrame()
    {
        var detector = CreateDetector();
        var signal = detector.Analyze(VoiceSamples(), isAssistantSpeaking: false);
        signal.Action.Should().Be(TurnAction.SpeechStarted);
        signal.Confidence.Should().Be(1.0f);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenSilenceAndIdle()
    {
        var detector = CreateDetector();
        var signal = detector.Analyze(SilenceSamples(), isAssistantSpeaking: false);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenVoiceContinuesAfterSpeechStarted()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        var signal = detector.Analyze(VoiceSamples(), false);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldReturnEndOfUtterance_WhenSilenceExceedsThreshold()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        var signal = detector.Analyze(SilenceSamples(), false);
        signal.Action.Should().Be(TurnAction.EndOfUtterance);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenSilenceBelowThreshold()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        var signal = detector.Analyze(SilenceSamples(), false);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldReturnEndOfUtterance_WhenMaxUtteranceDurationExceeded()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false); // SpeechStarted
        detector.Analyze(VoiceSamples(), false); // 40ms
        detector.Analyze(VoiceSamples(), false); // 60ms
        detector.Analyze(VoiceSamples(), false); // 80ms
        var signal = detector.Analyze(VoiceSamples(), false); // 100ms -> EndOfUtterance
        signal.Action.Should().Be(TurnAction.EndOfUtterance);
    }

    [Fact]
    public void Analyze_ShouldReturnBargIn_WhenVoiceExceedsThresholdDuringTts()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), isAssistantSpeaking: true);
        var signal = detector.Analyze(VoiceSamples(), isAssistantSpeaking: true);
        signal.Action.Should().Be(TurnAction.BargIn);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenVoiceBelowBargInThreshold()
    {
        var detector = CreateDetector();
        var signal = detector.Analyze(VoiceSamples(), isAssistantSpeaking: true);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldResetVoiceDuration_WhenSilenceDuringTts()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), isAssistantSpeaking: true);
        detector.Analyze(SilenceSamples(), isAssistantSpeaking: true);
        var signal = detector.Analyze(VoiceSamples(), isAssistantSpeaking: true);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenSilenceDuringTts()
    {
        var detector = CreateDetector();
        var signal = detector.Analyze(SilenceSamples(), isAssistantSpeaking: true);
        signal.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Reset_ShouldClearAllState()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        detector.Analyze(VoiceSamples(), false);
        detector.Reset();
        var signal = detector.Analyze(VoiceSamples(), false);
        signal.Action.Should().Be(TurnAction.SpeechStarted);
    }

    [Fact]
    public void Analyze_ShouldReturnSpeechStarted_AfterEndOfUtteranceAndNewVoice()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        var signal = detector.Analyze(VoiceSamples(), false);
        signal.Action.Should().Be(TurnAction.SpeechStarted);
    }

    [Fact]
    public void Analyze_ShouldResetSilence_WhenVoiceResumesBeforeThreshold()
    {
        var detector = CreateDetector();
        detector.Analyze(VoiceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        detector.Analyze(VoiceSamples(), false);
        detector.Analyze(SilenceSamples(), false);
        var signal = detector.Analyze(SilenceSamples(), false);
        signal.Action.Should().Be(TurnAction.Continue);
    }
}

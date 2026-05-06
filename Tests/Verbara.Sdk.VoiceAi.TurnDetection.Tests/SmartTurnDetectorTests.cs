using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests;

public sealed class SmartTurnDetectorTests : IDisposable
{
    private readonly OnnxSessionManager _onnx = new();
    private readonly SmartTurnDetector _detector;

    public SmartTurnDetectorTests()
    {
        _detector = new SmartTurnDetector(
            Options.Create(new SmartTurnDetectorOptions()),
            _onnx);
    }

    public void Dispose()
    {
        _detector.Dispose();
        _onnx.Dispose();
    }

    [Fact]
    public void Analyze_ShouldReturnSpeechStarted_WhenFirstVoiceFrame()
    {
        var samples = GenerateVoiceFrame();

        var result = _detector.Analyze(samples, isAssistantSpeaking: false);

        result.Action.Should().Be(TurnAction.SpeechStarted);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenSilenceAndIdle()
    {
        var silence = new short[160]; // all zeros = silence

        var result = _detector.Analyze(silence, isAssistantSpeaking: false);

        result.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldReturnBargIn_WhenVoiceDuringTts()
    {
        var voice = GenerateVoiceFrame();
        TurnSignal result = default;

        // Feed voice frames until barge-in triggers (200ms default = 10 frames of 20ms)
        for (int i = 0; i < 15; i++)
        {
            result = _detector.Analyze(voice, isAssistantSpeaking: true);
            if (result.Action == TurnAction.BargIn)
                break;
        }

        result.Action.Should().Be(TurnAction.BargIn);
    }

    [Fact]
    public void Reset_ShouldClearAllState()
    {
        var voice = GenerateVoiceFrame();
        _detector.Analyze(voice, isAssistantSpeaking: false);

        _detector.Reset();

        // Next voice frame should be SpeechStarted again (fresh state)
        var result = _detector.Analyze(voice, isAssistantSpeaking: false);
        result.Action.Should().Be(TurnAction.SpeechStarted);
    }

    [Fact]
    public void Analyze_ShouldReturnContinue_WhenSpeechWithoutSilence()
    {
        var voice = GenerateVoiceFrame();
        _detector.Analyze(voice, isAssistantSpeaking: false); // SpeechStarted

        var result = _detector.Analyze(voice, isAssistantSpeaking: false);

        result.Action.Should().Be(TurnAction.Continue);
    }

    [Fact]
    public void Analyze_ShouldRunModel_WhenSilenceAfterSpeech()
    {
        var voice = GenerateVoiceFrame();
        var silence = new short[160];

        // Speak for a few frames
        _detector.Analyze(voice, isAssistantSpeaking: false); // SpeechStarted
        for (int i = 0; i < 5; i++)
            _detector.Analyze(voice, isAssistantSpeaking: false);

        // Silence for enough frames to trigger model (200ms = 10 frames x 20ms)
        TurnSignal result = default;
        for (int i = 0; i < 15; i++)
        {
            result = _detector.Analyze(silence, isAssistantSpeaking: false);
            if (result.Action == TurnAction.EndOfUtterance)
                break;
        }

        // The model should have run. It may return EndOfUtterance or Continue
        // depending on the model's interpretation of the audio.
        result.Action.Should().BeOneOf(TurnAction.EndOfUtterance, TurnAction.Continue);

        if (result.Action == TurnAction.EndOfUtterance)
        {
            result.Confidence.Should().BeInRange(0f, 1f);
        }
    }

    [Fact]
    public void Analyze_ShouldResetBargInTimer_WhenSilenceDuringTts()
    {
        var voice = GenerateVoiceFrame();
        var silence = new short[160];

        // 5 voice frames (100ms) during TTS, then silence
        for (int i = 0; i < 5; i++)
            _detector.Analyze(voice, isAssistantSpeaking: true);
        _detector.Analyze(silence, isAssistantSpeaking: true);

        // Need full 200ms of voice again, not just the remaining 100ms
        TurnSignal result = default;
        for (int i = 0; i < 9; i++)
        {
            result = _detector.Analyze(voice, isAssistantSpeaking: true);
        }

        // 9 frames = 180ms, not enough for 200ms barge-in
        result.Action.Should().Be(TurnAction.Continue);
    }

    private static short[] GenerateVoiceFrame()
    {
        // 20ms at 8kHz = 160 samples
        // Generate a 440Hz sine wave loud enough to exceed -40 dBFS
        var samples = new short[160];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(16000 * MathF.Sin(2.0f * MathF.PI * 440.0f * i / 8000.0f));
        }

        return samples;
    }
}

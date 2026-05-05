using Verbara.Sdk.Audio.Processing;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Threshold-based turn detector using RMS energy silence detection,
/// timer-based end-of-utterance, and timer-based barge-in.
/// </summary>
public sealed class SilenceTurnDetector : ITurnDetector
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    private readonly double _silenceThresholdDb;
    private readonly TimeSpan _endOfUtteranceSilence;
    private readonly TimeSpan _bargInVoiceThreshold;
    private readonly TimeSpan _maxUtteranceDuration;

    private TimeSpan _silenceDuration;
    private TimeSpan _voiceDuration;
    private TimeSpan _utteranceDuration;
    private bool _isSpeaking;

    /// <summary>Creates a new silence-based turn detector with the given pipeline options.</summary>
    public SilenceTurnDetector(IOptions<VoiceAiPipelineOptions> options)
    {
        var opts = options.Value;
        _silenceThresholdDb = opts.SilenceThresholdDb;
        _endOfUtteranceSilence = opts.EndOfUtteranceSilence;
        _bargInVoiceThreshold = opts.BargInVoiceThreshold;
        _maxUtteranceDuration = opts.MaxUtteranceDuration;
    }

    /// <inheritdoc />
    public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
    {
        bool silence = AudioProcessor.IsSilence(samples, _silenceThresholdDb);

        if (isAssistantSpeaking)
            return AnalyzeDuringTts(silence);

        return AnalyzeDuringListening(silence);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _silenceDuration = TimeSpan.Zero;
        _voiceDuration = TimeSpan.Zero;
        _utteranceDuration = TimeSpan.Zero;
        _isSpeaking = false;
    }

    private TurnSignal AnalyzeDuringTts(bool silence)
    {
        if (!silence)
        {
            _voiceDuration += FrameDuration;
            if (_voiceDuration >= _bargInVoiceThreshold)
            {
                _voiceDuration = TimeSpan.Zero;
                if (!_isSpeaking)
                {
                    _isSpeaking = true;
                    _utteranceDuration = TimeSpan.Zero;
                }
                _utteranceDuration += FrameDuration;
                return new TurnSignal(TurnAction.BargIn);
            }
        }
        else
        {
            _voiceDuration = TimeSpan.Zero;
        }

        return new TurnSignal(TurnAction.Continue);
    }

    private TurnSignal AnalyzeDuringListening(bool silence)
    {
        if (!silence)
        {
            _silenceDuration = TimeSpan.Zero;

            if (!_isSpeaking)
            {
                _isSpeaking = true;
                _utteranceDuration = FrameDuration;
                return new TurnSignal(TurnAction.SpeechStarted);
            }

            _utteranceDuration += FrameDuration;
            if (_utteranceDuration >= _maxUtteranceDuration)
            {
                _isSpeaking = false;
                _utteranceDuration = TimeSpan.Zero;
                _silenceDuration = TimeSpan.Zero;
                return new TurnSignal(TurnAction.EndOfUtterance);
            }

            return new TurnSignal(TurnAction.Continue);
        }

        if (_isSpeaking)
        {
            _silenceDuration += FrameDuration;
            if (_silenceDuration >= _endOfUtteranceSilence)
            {
                _isSpeaking = false;
                _silenceDuration = TimeSpan.Zero;
                _utteranceDuration = TimeSpan.Zero;
                return new TurnSignal(TurnAction.EndOfUtterance);
            }
        }

        return new TurnSignal(TurnAction.Continue);
    }
}

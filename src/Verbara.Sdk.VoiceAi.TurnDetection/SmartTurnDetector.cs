using Verbara.Sdk.Audio.Processing;
using Verbara.Sdk.Audio.Resampling;
using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.TurnDetection;

/// <summary>
/// ML-based turn detector using the Pipecat smart-turn-v3.2-cpu ONNX model.
/// Detects semantic end-of-turn boundaries, not just silence pauses.
/// </summary>
public sealed class SmartTurnDetector : ITurnDetector, IDisposable
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    private const int ModelSampleRate = 16000;
    private const int InputSampleRate = 8000;
    private const int RingBufferCapacity = ModelSampleRate * 8; // 8 seconds at 16kHz

    private readonly SmartTurnDetectorOptions _options;
    private readonly OnnxSessionManager _sessionManager;
    private readonly MelSpectrogram _melExtractor;
    private readonly RingBuffer _audioBuffer;
    private readonly PolyphaseResampler _resampler;

    private TimeSpan _silenceDuration;
    private TimeSpan _voiceDuration;
    private TimeSpan _utteranceDuration;
    private bool _isSpeaking;
    private bool _hasPendingSpeech;

    /// <summary>Creates a new smart turn detector with the given options and ONNX session manager.</summary>
    internal SmartTurnDetector(IOptions<SmartTurnDetectorOptions> options, OnnxSessionManager sessionManager)
    {
        _options = options.Value;
        _sessionManager = sessionManager;
        _melExtractor = new MelSpectrogram();
        _audioBuffer = new RingBuffer(RingBufferCapacity);
        _resampler = ResamplerFactory.Create(InputSampleRate, ModelSampleRate);
    }

    /// <inheritdoc />
    public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
    {
        if (isAssistantSpeaking)
            return AnalyzeBargIn(samples);

        return AnalyzeTurn(samples);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _silenceDuration = TimeSpan.Zero;
        _voiceDuration = TimeSpan.Zero;
        _utteranceDuration = TimeSpan.Zero;
        _isSpeaking = false;
        _hasPendingSpeech = false;
        _audioBuffer.Clear();
        _resampler.Reset();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _resampler.Dispose();
    }

    private TurnSignal AnalyzeBargIn(ReadOnlySpan<short> samples)
    {
        bool silence = AudioProcessor.IsSilence(samples, _options.SilenceThresholdDb);

        if (!silence)
        {
            _voiceDuration += FrameDuration;
            if (_voiceDuration >= _options.BargInVoiceThreshold)
            {
                _voiceDuration = TimeSpan.Zero;
                return new TurnSignal(TurnAction.BargIn);
            }
        }
        else
        {
            _voiceDuration = TimeSpan.Zero;
        }

        return new TurnSignal(TurnAction.Continue);
    }

    private TurnSignal AnalyzeTurn(ReadOnlySpan<short> samples)
    {
        bool isSilence = AudioProcessor.IsSilence(samples, _options.SilenceThresholdDb);

        if (!isSilence)
        {
            // Upsample 8kHz→16kHz and append to ring buffer
            AppendResampled(samples);

            _silenceDuration = TimeSpan.Zero;

            if (!_isSpeaking)
            {
                _isSpeaking = true;
                _hasPendingSpeech = true;
                _utteranceDuration = FrameDuration;
                return new TurnSignal(TurnAction.SpeechStarted);
            }

            _utteranceDuration += FrameDuration;
            if (_utteranceDuration >= _options.MaxUtteranceDuration)
            {
                _isSpeaking = false;
                _hasPendingSpeech = false;
                _utteranceDuration = TimeSpan.Zero;
                _audioBuffer.Clear();
                return new TurnSignal(TurnAction.EndOfUtterance);
            }

            _hasPendingSpeech = true;
            return new TurnSignal(TurnAction.Continue);
        }

        // Silence detected
        if (_isSpeaking)
        {
            _silenceDuration += FrameDuration;

            if (_silenceDuration >= _options.SilenceTriggerDuration && _hasPendingSpeech)
            {
                // Run ONNX model on accumulated audio
                var audio = new float[_audioBuffer.Count];
                _audioBuffer.CopyTo(audio);
                var mel = _melExtractor.Compute(audio);
                var prob = _sessionManager.RunInference(mel);

                if (prob >= _options.TurnConfidenceThreshold)
                {
                    _isSpeaking = false;
                    _hasPendingSpeech = false;
                    _utteranceDuration = TimeSpan.Zero;
                    _audioBuffer.Clear();
                    return new TurnSignal(TurnAction.EndOfUtterance, prob);
                }

                // Model says "not a turn" — reset silence timer, keep listening
                _hasPendingSpeech = false;
                _silenceDuration = TimeSpan.Zero;
            }
        }

        return new TurnSignal(TurnAction.Continue);
    }

    private void AppendResampled(ReadOnlySpan<short> samples)
    {
        // Upsample 8kHz PCM16 → 16kHz PCM16
        int maxOutput = ResamplerFactory.CalculateOutputSize(samples.Length, InputSampleRate, ModelSampleRate);
        Span<short> resampled = stackalloc short[maxOutput];
        int outputSamples = _resampler.Process(samples, resampled);

        // Convert PCM16 → float32 and write to ring buffer
        Span<float> floatSamples = stackalloc float[outputSamples];
        AudioProcessor.ConvertToFloat32(resampled[..outputSamples], floatSamples);
        _audioBuffer.Write(floatSamples);
    }
}

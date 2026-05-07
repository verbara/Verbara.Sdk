using System.ComponentModel.DataAnnotations;

namespace Verbara.Sdk.VoiceAi.TurnDetection;

/// <summary>Configuration for the smart turn detector.</summary>
public sealed class SmartTurnDetectorOptions
{
    /// <summary>Probability threshold above which a pause is classified as end-of-turn. Default 0.5.</summary>
    [Range(0.0f, 1.0f)]
    public float TurnConfidenceThreshold { get; set; } = 0.5f;

    /// <summary>RMS energy threshold (dBFS) for silence detection. Default -40.0.</summary>
    [Range(-100.0, 0.0)]
    public double SilenceThresholdDb { get; set; } = -40.0;

    /// <summary>Duration of silence after speech before running the model. Default 200ms.</summary>
    public TimeSpan SilenceTriggerDuration { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Duration of voice during TTS playback to trigger barge-in. Default 200ms.</summary>
    public TimeSpan BargInVoiceThreshold { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum utterance duration before forcing end-of-turn. Default 30 seconds.</summary>
    public TimeSpan MaxUtteranceDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>ONNX Runtime execution provider. Default CPU.</summary>
    public ExecutionProvider ExecutionProvider { get; set; } = ExecutionProvider.Cpu;

    /// <summary>Number of intra-op threads for ONNX Runtime. Default 1.</summary>
    [Range(1, 64)]
    public int IntraOpThreads { get; set; } = 1;
}

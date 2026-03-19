using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

/// <summary>Configuration options for the Voice AI pipeline.</summary>
public sealed class VoiceAiPipelineOptions
{
    /// <summary>Audio format of incoming frames from Asterisk.</summary>
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;

    /// <summary>Audio format for TTS output sent back to Asterisk.</summary>
    public AudioFormat OutputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;

    /// <summary>Energy threshold (dBFS) below which a frame is considered silence.</summary>
    public double SilenceThresholdDb { get; set; } = -40.0;

    /// <summary>Duration of silence required to mark the end of an utterance.</summary>
    public TimeSpan EndOfUtteranceSilence { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Duration of continuous voice during TTS playback required to trigger barge-in.</summary>
    public TimeSpan BargInVoiceThreshold { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum duration of a single utterance before forcing STT.</summary>
    public TimeSpan MaxUtteranceDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of conversation turns to keep in history.</summary>
    public int MaxHistoryTurns { get; set; } = 20;
}

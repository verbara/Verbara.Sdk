namespace Asterisk.Sdk.VoiceAi.Events;

/// <summary>Base record for all events emitted by the Voice AI pipeline.</summary>
public abstract record VoiceAiPipelineEvent(DateTimeOffset Timestamp);

/// <summary>The caller started speaking (VAD triggered).</summary>
public record SpeechStartedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>The caller stopped speaking.</summary>
public record SpeechEndedEvent(DateTimeOffset Timestamp, TimeSpan Duration)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>A transcript (partial or final) was received from STT.</summary>
public record TranscriptReceivedEvent(
    DateTimeOffset Timestamp,
    string Transcript,
    float Confidence,
    bool IsFinal)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>The conversation handler produced a response.</summary>
public record ResponseGeneratedEvent(DateTimeOffset Timestamp, string Response)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>TTS synthesis has started for a response.</summary>
public record SynthesisStartedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>TTS synthesis completed.</summary>
public record SynthesisEndedEvent(DateTimeOffset Timestamp, TimeSpan Duration)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>The caller interrupted the assistant (barge-in).</summary>
public record BargInDetectedEvent(DateTimeOffset Timestamp)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>An error occurred in one of the pipeline stages.</summary>
public record PipelineErrorEvent(
    DateTimeOffset Timestamp,
    Exception Error,
    PipelineErrorSource Source)
    : VoiceAiPipelineEvent(Timestamp);

/// <summary>Identifies which pipeline stage produced an error.</summary>
public enum PipelineErrorSource
{
    /// <summary>Speech-to-text stage.</summary>
    Stt,

    /// <summary>Text-to-speech stage.</summary>
    Tts,

    /// <summary>Conversation handler stage.</summary>
    Handler
}

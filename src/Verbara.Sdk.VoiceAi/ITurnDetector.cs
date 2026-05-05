namespace Verbara.Sdk.VoiceAi;

/// <summary>Identifies a turn-detection decision for a single audio frame.</summary>
public enum TurnAction
{
    /// <summary>No state change. Continue current behavior (buffering if speaking, waiting if idle).</summary>
    Continue,

    /// <summary>Speech onset detected. Start buffering audio.</summary>
    SpeechStarted,

    /// <summary>End of utterance detected. Flush buffer to STT.</summary>
    EndOfUtterance,

    /// <summary>Barge-in detected (user spoke over assistant). Cancel TTS and start buffering.</summary>
    BargIn
}

/// <summary>Result of analyzing a single audio frame for turn boundaries.</summary>
/// <param name="Action">The detected turn action.</param>
/// <param name="Confidence">Confidence score (0.0-1.0). Always 1.0 for threshold-based detectors.</param>
public readonly record struct TurnSignal(TurnAction Action, float Confidence = 1.0f);

/// <summary>
/// Analyzes audio frames and detects turn boundaries: speech start, end-of-utterance,
/// and barge-in. Implementations are stateful and must be scoped per session.
/// </summary>
public interface ITurnDetector
{
    /// <summary>Analyze a single audio frame and return a turn signal.</summary>
    /// <param name="samples">PCM16 samples for the current frame.</param>
    /// <param name="isAssistantSpeaking">True when TTS playback is active.</param>
    TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking);

    /// <summary>Reset internal state. Called between sessions or for testing.</summary>
    void Reset();
}

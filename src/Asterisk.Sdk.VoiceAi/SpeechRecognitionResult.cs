namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// A single recognition result from the STT engine, which may be partial
/// (interim hypothesis) or final (committed transcript).
/// </summary>
public readonly record struct SpeechRecognitionResult(
    string Transcript,
    float Confidence,
    bool IsFinal,
    TimeSpan Duration);

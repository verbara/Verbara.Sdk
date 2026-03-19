namespace Asterisk.Sdk.VoiceAi.Pipeline;

internal enum PipelineState
{
    Idle,
    Listening,
    Recognizing,
    Handling,
    Speaking,
    Interrupted
}

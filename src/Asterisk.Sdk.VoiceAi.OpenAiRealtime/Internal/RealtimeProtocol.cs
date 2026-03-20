namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

/// <summary>OpenAI Realtime API event type name constants.</summary>
internal static class RealtimeProtocol
{
    // Inbound (OpenAI -> client)
    public const string SessionCreated                    = "session.created";
    public const string ResponseAudioDelta                = "response.audio.delta";
    public const string ResponseAudioDone                 = "response.audio.done";
    public const string ResponseAudioTranscriptDelta      = "response.audio_transcript.delta";
    public const string ResponseAudioTranscriptDone       = "response.audio_transcript.done";
    public const string ResponseCreated                   = "response.created";
    public const string ResponseDone                      = "response.done";
    public const string ResponseCancelled                 = "response.cancelled";
    public const string ResponseFunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string InputAudioBufferSpeechStarted     = "input_audio_buffer.speech_started";
    public const string InputAudioBufferSpeechStopped     = "input_audio_buffer.speech_stopped";
    public const string Error                             = "error";

    // Outbound (client -> OpenAI)
    public const string SessionUpdate           = "session.update";
    public const string InputAudioBufferAppend  = "input_audio_buffer.append";
    public const string InputAudioBufferCommit  = "input_audio_buffer.commit";
    public const string ConversationItemCreate  = "conversation.item.create";
    public const string ResponseCreate          = "response.create";
}

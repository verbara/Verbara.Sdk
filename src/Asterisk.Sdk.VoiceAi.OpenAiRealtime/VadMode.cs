namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Voice Activity Detection mode for the OpenAI Realtime session.</summary>
public enum VadMode
{
    /// <summary>OpenAI detects speech boundaries server-side (default, recommended).</summary>
    ServerSide,

    /// <summary>
    /// VAD disabled — caller must send <c>input_audio_buffer.commit</c> manually.
    /// Use only when driving turn boundaries externally.
    /// </summary>
    Disabled
}

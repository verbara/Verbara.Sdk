namespace Verbara.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Voice Activity Detection mode for the OpenAI Realtime session.</summary>
public enum VadMode
{
    /// <summary>OpenAI detects speech boundaries server-side (default, recommended).</summary>
    ServerSide,

    /// <summary>
    /// VAD disabled: the session is opened with <c>turn_detection</c> set to <c>null</c>, so OpenAI detects no end
    /// of turn. This package sends no <c>input_audio_buffer.commit</c> and exposes no member that does, so in this
    /// mode the model does not answer the caller's audio. Use <see cref="ServerSide"/>.
    /// </summary>
    Disabled
}

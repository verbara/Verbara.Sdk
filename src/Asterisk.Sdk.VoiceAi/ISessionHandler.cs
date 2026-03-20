using Asterisk.Sdk.VoiceAi.AudioSocket;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Handles a single AudioSocket session end-to-end.
/// Implementations include <see cref="Pipeline.VoiceAiPipeline"/> (turn-based STT+LLM+TTS)
/// and <c>OpenAiRealtimeBridge</c> (streaming WebSocket to OpenAI Realtime API).
/// </summary>
public interface ISessionHandler
{
    /// <summary>
    /// Runs the session until the AudioSocket disconnects or <paramref name="ct"/> is cancelled.
    /// </summary>
    ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);
}

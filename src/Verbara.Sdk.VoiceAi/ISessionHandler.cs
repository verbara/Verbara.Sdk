using Verbara.Sdk.VoiceAi.AudioSocket;

namespace Verbara.Sdk.VoiceAi;

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
    /// <remarks>
    /// When the session was handed on by <see cref="Pipeline.VoiceAiSessionBroker"/>, the broker ends it
    /// as soon as this method completes, whether it returned, threw or was cancelled: the far end receives
    /// one hangup frame, if the session is still live, and then the close. Do not return before the work
    /// on the session is done — await it instead — or the line is ended under it. A graceful host stop
    /// also ends a live session this way while this method still runs, and then waits for it to return.
    /// </remarks>
    ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default);
}

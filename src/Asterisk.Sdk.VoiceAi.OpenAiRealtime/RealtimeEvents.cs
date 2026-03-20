namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Base record for all Realtime bridge observability events.</summary>
/// <param name="ChannelId">Identifies which AudioSocket session produced this event.</param>
/// <param name="Timestamp">UTC wall-clock time when the event was created.</param>
public abstract record RealtimeEvent(Guid ChannelId, DateTimeOffset Timestamp);

/// <summary>OpenAI detected that the caller started speaking.</summary>
public sealed record RealtimeSpeechStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI detected that the caller stopped speaking.</summary>
public sealed record RealtimeSpeechStoppedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>A transcript fragment or complete transcript was received from OpenAI.</summary>
/// <param name="ChannelId">Identifies which AudioSocket session produced this event.</param>
/// <param name="Timestamp">UTC wall-clock time when the event was created.</param>
/// <param name="Transcript">The partial or final transcript text.</param>
/// <param name="IsFinal"><c>true</c> when this is the complete, final transcript for the turn.</param>
public sealed record RealtimeTranscriptEvent(
    Guid ChannelId, DateTimeOffset Timestamp, string Transcript, bool IsFinal)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI started generating a response.</summary>
public sealed record RealtimeResponseStartedEvent(Guid ChannelId, DateTimeOffset Timestamp)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>OpenAI finished (or cancelled) a response.</summary>
/// <param name="ChannelId">Identifies which AudioSocket session produced this event.</param>
/// <param name="Timestamp">UTC wall-clock time when the event was created.</param>
/// <param name="Duration">Wall-clock time from <c>response.created</c> to <c>response.done</c>/<c>response.cancelled</c>.</param>
public sealed record RealtimeResponseEndedEvent(
    Guid ChannelId, DateTimeOffset Timestamp, TimeSpan Duration)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>A function tool was invoked by OpenAI and its result was sent back.</summary>
public sealed record RealtimeFunctionCalledEvent(
    Guid ChannelId, DateTimeOffset Timestamp,
    string FunctionName, string ArgumentsJson, string ResultJson)
    : RealtimeEvent(ChannelId, Timestamp);

/// <summary>An error event was received from OpenAI.</summary>
public sealed record RealtimeErrorEvent(Guid ChannelId, DateTimeOffset Timestamp, string Message)
    : RealtimeEvent(ChannelId, Timestamp);

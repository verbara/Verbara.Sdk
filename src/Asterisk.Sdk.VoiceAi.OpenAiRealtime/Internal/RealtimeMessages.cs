namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

// ─── Outbound (client → OpenAI) ───────────────────────────────────────────────
// Note: session.update is built via Utf8JsonWriter in OpenAiRealtimeBridge.BuildSessionUpdate().
// Only the simpler outbound messages below use JsonSerializer + RealtimeJsonContext.

internal sealed class InputAudioBufferAppendRequest
{
    public string Type { get; init; } = RealtimeProtocol.InputAudioBufferAppend;
    public string Audio { get; set; } = "";
}

internal sealed class InputAudioBufferCommitRequest
{
    public string Type { get; init; } = RealtimeProtocol.InputAudioBufferCommit;
}

internal sealed class ConversationItemCreateRequest
{
    public string Type { get; init; } = RealtimeProtocol.ConversationItemCreate;
    public ConversationItem Item { get; set; } = default!;
}

internal sealed class ConversationItem
{
    public string Type { get; set; } = "";
    public string? CallId { get; set; }
    public string? Output { get; set; }
}

internal sealed class ResponseCreateRequest
{
    public string Type { get; init; } = RealtimeProtocol.ResponseCreate;
}

// ─── Inbound (OpenAI → client) ────────────────────────────────────────────────
// Only the fields the bridge actually reads are mapped — extra fields are ignored.

internal sealed class ServerEventBase
{
    public string Type { get; set; } = "";
}

internal sealed class ResponseAudioDeltaEvent
{
    public string Type { get; set; } = "";
    public string Delta { get; set; } = "";
}

internal sealed class ResponseAudioTranscriptDeltaEvent
{
    public string Type { get; set; } = "";
    public string Delta { get; set; } = "";
}

internal sealed class ResponseAudioTranscriptDoneEvent
{
    public string Type { get; set; } = "";
    public string Transcript { get; set; } = "";
}

internal sealed class FunctionCallArgumentsDoneEvent
{
    public string Type { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

internal sealed class ServerErrorEvent
{
    public string Type { get; set; } = "";
    public ServerError? Error { get; set; }
}

internal sealed class ServerError
{
    public string Message { get; set; } = "";
}

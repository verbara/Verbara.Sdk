using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

// session.update is built with Utf8JsonWriter — its types are NOT included here.
// Only types that pass through JsonSerializer.Deserialize / JsonSerializer.Serialize
// need [JsonSerializable] entries.
[JsonSerializable(typeof(InputAudioBufferAppendRequest))]
[JsonSerializable(typeof(InputAudioBufferCommitRequest))]
[JsonSerializable(typeof(ConversationItemCreateRequest))]
[JsonSerializable(typeof(ConversationItem))]
[JsonSerializable(typeof(ResponseCreateRequest))]
[JsonSerializable(typeof(ServerEventBase))]
[JsonSerializable(typeof(ResponseAudioDeltaEvent))]
[JsonSerializable(typeof(ResponseAudioTranscriptDeltaEvent))]
[JsonSerializable(typeof(ResponseAudioTranscriptDoneEvent))]
[JsonSerializable(typeof(FunctionCallArgumentsDoneEvent))]
[JsonSerializable(typeof(ServerErrorEvent))]
[JsonSerializable(typeof(ServerError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class RealtimeJsonContext : JsonSerializerContext;

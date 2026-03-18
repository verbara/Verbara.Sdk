using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;

// === JSON Context (must be in the AOT project to verify source generation) ===

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AotSessionSnapshot))]
[JsonSerializable(typeof(SessionParticipant))]
[JsonSerializable(typeof(CallSessionEvent))]
[JsonSerializable(typeof(List<SessionParticipant>))]
[JsonSerializable(typeof(List<CallSessionEvent>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CallSessionState))]
[JsonSerializable(typeof(CallSessionEventType))]
[JsonSerializable(typeof(CallDirection))]
[JsonSerializable(typeof(ParticipantRole))]
[JsonSerializable(typeof(HangupCause))]
internal sealed partial class AotJsonContext : JsonSerializerContext;

// Minimal snapshot for AOT validation
internal sealed class AotSessionSnapshot
{
    public required string SessionId { get; init; }
    public CallSessionState State { get; init; }
    public CallDirection Direction { get; init; }
    public HangupCause? HangupCause { get; init; }
    public List<SessionParticipant> Participants { get; init; } = [];
    public List<CallSessionEvent> Events { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

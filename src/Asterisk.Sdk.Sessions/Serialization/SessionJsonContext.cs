using System.Text.Json.Serialization;
using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk.Sessions.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CallSessionSnapshot))]
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
internal sealed partial class SessionJsonContext : JsonSerializerContext;

namespace Asterisk.Sdk.Push.AspNetCore;

using System.Text.Json.Serialization;
using Asterisk.Sdk.Push.Events;

/// <summary>
/// AOT-safe JSON serializer context for SSE event serialization.
/// </summary>
[JsonSerializable(typeof(PushEvent))]
[JsonSerializable(typeof(PushEventMetadata))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SseJsonContext : JsonSerializerContext;

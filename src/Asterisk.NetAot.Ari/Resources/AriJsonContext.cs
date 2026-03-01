using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ari.Resources;

/// <summary>
/// Source-generated JSON context for AOT-compatible serialization of ARI models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AriChannel))]
[JsonSerializable(typeof(AriBridge))]
[JsonSerializable(typeof(AriChannel[]))]
[JsonSerializable(typeof(AriBridge[]))]
public sealed partial class AriJsonContext : JsonSerializerContext;

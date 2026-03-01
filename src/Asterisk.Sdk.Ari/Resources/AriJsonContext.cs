using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>
/// Source-generated JSON context for AOT-compatible serialization of ARI models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AriChannel))]
[JsonSerializable(typeof(AriBridge))]
[JsonSerializable(typeof(AriChannel[]))]
[JsonSerializable(typeof(AriBridge[]))]
[JsonSerializable(typeof(AriChannelState))]
public sealed partial class AriJsonContext : JsonSerializerContext;

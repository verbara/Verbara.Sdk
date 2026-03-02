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
[JsonSerializable(typeof(AriChannel[]))]
[JsonSerializable(typeof(AriBridge))]
[JsonSerializable(typeof(AriBridge[]))]
[JsonSerializable(typeof(AriChannelState))]
[JsonSerializable(typeof(AriPlayback))]
[JsonSerializable(typeof(AriPlayback[]))]
[JsonSerializable(typeof(AriLiveRecording))]
[JsonSerializable(typeof(AriLiveRecording[]))]
[JsonSerializable(typeof(AriStoredRecording))]
[JsonSerializable(typeof(AriStoredRecording[]))]
[JsonSerializable(typeof(AriEndpoint))]
[JsonSerializable(typeof(AriEndpoint[]))]
[JsonSerializable(typeof(AriApplication))]
[JsonSerializable(typeof(AriApplication[]))]
[JsonSerializable(typeof(AriSound))]
[JsonSerializable(typeof(AriSound[]))]
[JsonSerializable(typeof(AriFormatLang))]
[JsonSerializable(typeof(AriFormatLang[]))]
public sealed partial class AriJsonContext : JsonSerializerContext;

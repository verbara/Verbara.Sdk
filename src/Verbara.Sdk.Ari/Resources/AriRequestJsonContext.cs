using System.Text.Json.Serialization;

namespace Verbara.Sdk.Ari.Resources;

/// <summary>
/// Source-generated JSON context for the request bodies the ARI resources write. Internal, unlike
/// <see cref="AriJsonContext"/>, whose every registered type is public surface.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AriExternalMediaBody))]
internal sealed partial class AriRequestJsonContext : JsonSerializerContext;

/// <summary>
/// The body of an external media create that carries channel variables: <c>{"variables":{…}}</c>. The keys are
/// written as given; the naming policy applies to property names only.
/// </summary>
internal sealed record AriExternalMediaBody(IReadOnlyDictionary<string, string> Variables);

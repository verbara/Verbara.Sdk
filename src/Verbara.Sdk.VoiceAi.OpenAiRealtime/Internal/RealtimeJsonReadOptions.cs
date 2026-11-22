using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;

/// <summary>Enforces response member nullability without changing request serialization.</summary>
internal static class RealtimeJsonReadOptions
{
    internal static JsonSerializerOptions StrictOptions { get; } = CreateStrictOptions();

    /// <summary>
    /// Holds typed metadata once per DTO. A field access keeps the decode as the only invocation
    /// inside its guarded try, with no per-frame options lookup.
    /// </summary>
    internal static class For<T>
    {
        internal static readonly JsonTypeInfo<T> TypeInfo =
            (JsonTypeInfo<T>)StrictOptions.GetTypeInfo(typeof(T));
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        // Copy the generated resolver AND SnakeCaseLower naming: fresh options would silently
        // stop binding multi-word wire members. Sends retain their original contexts because
        // nullable annotation enforcement also affects writes.
        var options = new JsonSerializerOptions(RealtimeJsonContext.Default.Options)
        {
            RespectNullableAnnotations = true
        };
        options.MakeReadOnly();
        return options;
    }
}

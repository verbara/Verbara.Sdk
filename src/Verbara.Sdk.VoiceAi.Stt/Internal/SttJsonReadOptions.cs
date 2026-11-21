using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Verbara.Sdk.VoiceAi.Stt.Internal;

/// <summary>Enforces response member nullability without changing request serialization.</summary>
internal static class SttJsonReadOptions
{
    internal static JsonSerializerOptions StrictOptions { get; } = CreateStrictOptions();

    /// <summary>
    /// The strict metadata for <typeparamref name="T"/>, resolved once per type and held in a static
    /// field, so a receive loop pays no options lookup per frame.
    /// </summary>
    internal static JsonTypeInfo<T> TypeInfo<T>() => Cache<T>.Value;

    private static class Cache<T>
    {
        internal static readonly JsonTypeInfo<T> Value =
            (JsonTypeInfo<T>)StrictOptions.GetTypeInfo(typeof(T));
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        // Copy the generated resolver and wire settings; outbound serialization keeps using
        // the original context, because nullability enforcement also affects writes.
        var options = new JsonSerializerOptions(VoiceAiSttJsonContext.Default.Options)
        {
            RespectNullableAnnotations = true
        };
        options.MakeReadOnly();
        return options;
    }
}

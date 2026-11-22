using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Verbara.Sdk.VoiceAi.Tts.Internal;

/// <summary>Enforces response member nullability without changing request serialization.</summary>
internal static class TtsJsonReadOptions
{
    internal static JsonSerializerOptions StrictOptions { get; } = CreateStrictOptions();

    /// <summary>
    /// The strict metadata for <typeparamref name="T"/>, resolved once per type and held in a static
    /// readonly field, so a receive loop pays no options lookup per frame. It is a field rather than
    /// a method so that a decode <c>try</c> still makes exactly one invocation, the decode itself.
    /// </summary>
    internal static class For<T>
    {
        internal static readonly JsonTypeInfo<T> TypeInfo =
            (JsonTypeInfo<T>)StrictOptions.GetTypeInfo(typeof(T));
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        // Copy the generated resolver and wire settings; outbound serialization keeps using
        // the original context, because nullability enforcement also affects writes.
        var options = new JsonSerializerOptions(VoiceAiTtsJsonContext.Default.Options)
        {
            RespectNullableAnnotations = true
        };
        options.MakeReadOnly();
        return options;
    }
}

using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>AOT-safe validator for <see cref="OpenAiRealtimeOptions"/>.</summary>
[OptionsValidator]
public sealed partial class OpenAiRealtimeOptionsValidator : IValidateOptions<OpenAiRealtimeOptions> { }

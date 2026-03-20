using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>AOT-safe validator for <see cref="OpenAiRealtimeOptions"/>.</summary>
[OptionsValidator]
public sealed partial class OpenAiRealtimeOptionsValidator : IValidateOptions<OpenAiRealtimeOptions> { }

using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.TurnDetection;

/// <summary>AOT-safe validator for <see cref="SmartTurnDetectorOptions"/>.</summary>
[OptionsValidator]
public sealed partial class SmartTurnDetectorOptionsValidator : IValidateOptions<SmartTurnDetectorOptions> { }

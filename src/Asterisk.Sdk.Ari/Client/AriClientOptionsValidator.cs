using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// AOT-safe source-generated validator for <see cref="AriClientOptions"/>.
/// Replaces reflection-based <c>ValidateDataAnnotations()</c> to avoid IL2026 trim warnings.
/// </summary>
[OptionsValidator]
public partial class AriClientOptionsValidator : IValidateOptions<AriClientOptions>
{
}

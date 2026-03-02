using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ami.Connection;

/// <summary>
/// AOT-safe source-generated validator for <see cref="AmiConnectionOptions"/>.
/// Replaces reflection-based <c>ValidateDataAnnotations()</c> to avoid IL2026 trim warnings.
/// </summary>
[OptionsValidator]
public partial class AmiConnectionOptionsValidator : IValidateOptions<AmiConnectionOptions>
{
}

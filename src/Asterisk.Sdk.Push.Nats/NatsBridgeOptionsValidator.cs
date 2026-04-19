using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// AOT-safe source-generated validator for <see cref="NatsBridgeOptions"/>. Validates
/// DataAnnotations declared on the options type.
/// </summary>
[OptionsValidator]
public sealed partial class NatsBridgeOptionsValidator : IValidateOptions<NatsBridgeOptions>
{
}

/// <summary>
/// Additional validator enforcing rules that DataAnnotations cannot express at compile time:
/// <c>Url</c> must use the <c>nats://</c> scheme, and <c>SubjectPrefix</c> must contain no
/// whitespace or wildcard characters.
/// </summary>
internal sealed class NatsBridgeOptionsCustomValidator : IValidateOptions<NatsBridgeOptions>
{
    public ValidateOptionsResult Validate(string? name, NatsBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(capacity: 3);

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add($"{nameof(NatsBridgeOptions.Url)} must not be null or empty.");
        }
        else if (!options.Url.StartsWith("nats://", StringComparison.Ordinal))
        {
            failures.Add($"{nameof(NatsBridgeOptions.Url)} must start with 'nats://'.");
        }

        if (string.IsNullOrWhiteSpace(options.SubjectPrefix))
        {
            failures.Add($"{nameof(NatsBridgeOptions.SubjectPrefix)} must not be null or empty.");
        }
        else if (options.SubjectPrefix.Contains(' ', StringComparison.Ordinal)
            || options.SubjectPrefix.Contains('*', StringComparison.Ordinal)
            || options.SubjectPrefix.Contains('>', StringComparison.Ordinal))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.SubjectPrefix)} must not contain spaces or NATS wildcard characters ('*', '>').");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

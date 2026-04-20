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
        ValidateUrl(options, failures);
        ValidateSubjectPrefix(options, failures);
        if (options.Subscribe is { } sub)
        {
            ValidateSubscribe(sub, options.NodeId, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateUrl(NatsBridgeOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add($"{nameof(NatsBridgeOptions.Url)} must not be null or empty.");
        }
        else if (!options.Url.StartsWith("nats://", StringComparison.Ordinal))
        {
            failures.Add($"{nameof(NatsBridgeOptions.Url)} must start with 'nats://'.");
        }
    }

    private static void ValidateSubjectPrefix(NatsBridgeOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.SubjectPrefix))
        {
            failures.Add($"{nameof(NatsBridgeOptions.SubjectPrefix)} must not be null or empty.");
            return;
        }

        if (options.SubjectPrefix.Contains(' ', StringComparison.Ordinal)
            || options.SubjectPrefix.Contains('*', StringComparison.Ordinal)
            || options.SubjectPrefix.Contains('>', StringComparison.Ordinal))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.SubjectPrefix)} must not contain spaces or NATS wildcard characters ('*', '>').");
        }
    }

    private static void ValidateSubscribe(NatsSubscribeOptions sub, string? nodeId, List<string> failures)
    {
        ValidateSubjectFilters(sub.SubjectFilters, failures);

        if (sub.QueueGroup is not null && string.IsNullOrWhiteSpace(sub.QueueGroup))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.Subscribe)}.{nameof(NatsSubscribeOptions.QueueGroup)} must be null or a non-whitespace value.");
        }

        if (sub.SkipSelfOriginated && string.IsNullOrWhiteSpace(nodeId))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.NodeId)} must be set when {nameof(NatsBridgeOptions.Subscribe)}.{nameof(NatsSubscribeOptions.SkipSelfOriginated)} is true (required for loop prevention).");
        }
    }

    private static void ValidateSubjectFilters(string[]? filters, List<string> failures)
    {
        if (filters is null)
        {
            failures.Add($"{nameof(NatsBridgeOptions.Subscribe)}.{nameof(NatsSubscribeOptions.SubjectFilters)} must not be null.");
            return;
        }

        for (var i = 0; i < filters.Length; i++)
        {
            ValidateSubjectFilter(filters[i], i, failures);
        }
    }

    private static void ValidateSubjectFilter(string filter, int index, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.Subscribe)}.{nameof(NatsSubscribeOptions.SubjectFilters)}[{index}] must not be null or whitespace.");
        }
        else if (filter.Contains(' ', StringComparison.Ordinal))
        {
            failures.Add(
                $"{nameof(NatsBridgeOptions.Subscribe)}.{nameof(NatsSubscribeOptions.SubjectFilters)}[{index}] must not contain spaces.");
        }
    }
}

using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// What a host asks of every health check a Verbara.Sdk.Hosting registration installs (<c>ami</c>, <c>live</c>,
/// <c>agi</c>, <c>ari</c>, <c>sessions</c>, <c>verbara-pool</c>). Set it with
/// <c>services.Configure&lt;VerbaraHealthCheckOptions&gt;(o =&gt; o.Tags.Add("voice"))</c>, before or after the SDK's
/// registrations; <c>AddVerbara(IConfiguration)</c> also reads <c>Asterisk:HealthCheckTags</c> into it.
/// </summary>
public sealed class VerbaraHealthCheckOptions
{
    /// <summary>
    /// The tags every SDK health check carries. Empty by default: the SDK names no tag, so an endpoint filtered by tag
    /// sees none of its checks until you add one here. Never add the tag your readiness probe reads: an Asterisk outage
    /// would take the pod out of service. Every <c>Configure</c> that adds a tag, and the configuration key, contribute
    /// to one set, compared ignoring case; a <c>Configure</c> that calls <c>Clear</c> or <c>Remove</c> erases what the
    /// ones before it added, as with any options type. A null, empty or whitespace tag fails the host's start (or the
    /// first resolution of the health check service, whichever comes first) with an
    /// <see cref="OptionsValidationException"/> naming <c>Tags</c>.
    /// </summary>
    [HealthCheckTags]
    public ICollection<string> Tags { get; } = new List<string>();
}

/// <summary>
/// Source-generated (AOT-safe) validator for <see cref="VerbaraHealthCheckOptions"/>, run when the host starts and
/// whenever the options are first read. It applies <see cref="HealthCheckTagsAttribute"/> to
/// <see cref="VerbaraHealthCheckOptions.Tags"/>.
/// </summary>
[OptionsValidator]
internal partial class VerbaraHealthCheckOptionsValidator : IValidateOptions<VerbaraHealthCheckOptions>
{
}

/// <summary>
/// Fails on the first null, empty or whitespace health-check tag, naming the member and the tag's position. Any other
/// name is accepted: the SDK does not know which tag a host's endpoints read.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class HealthCheckTagsAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not IEnumerable tags)
            return ValidationResult.Success;

        var member = validationContext.MemberName ?? nameof(VerbaraHealthCheckOptions.Tags);
        var index = 0;
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag as string))
            {
                return new ValidationResult(
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}[{1}] is null, empty or whitespace; a health-check tag must have text.", member, index),
                    [member]);
            }

            index++;
        }

        return ValidationResult.Success;
    }
}

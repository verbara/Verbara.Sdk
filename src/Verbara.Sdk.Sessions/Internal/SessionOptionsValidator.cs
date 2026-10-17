using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Internal;

/// <summary>
/// Source-generated (AOT-safe) validator for <see cref="SessionOptions"/>, run when the host starts. Besides the
/// standard annotations it applies <see cref="WrapUpDurationRangeAttribute"/> to
/// <see cref="SessionOptions.WrapUpDuration"/>.
/// </summary>
[OptionsValidator]
internal partial class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
}

/// <summary>
/// Bounds <see cref="SessionOptions.WrapUpDuration"/>: from zero to <see cref="Max"/>, the largest due time the
/// system timer accepts. A negative value, <see cref="Timeout.InfiniteTimeSpan"/> included, fails, and so does a
/// longer one; the failure names the member.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class WrapUpDurationRangeAttribute : ValidationAttribute
{
    /// <summary>The largest due time <see cref="TimeProvider.System"/>'s timer accepts: 4 294 967 294 ms.</summary>
    internal static readonly TimeSpan Max = TimeSpan.FromMilliseconds(4_294_967_294);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not TimeSpan duration || (duration >= TimeSpan.Zero && duration <= Max))
            return ValidationResult.Success;

        var member = validationContext.MemberName ?? nameof(SessionOptions.WrapUpDuration);
        return new ValidationResult(
            string.Format(CultureInfo.InvariantCulture,
                "{0} must be from {1} to {2} ({3} ms), but was {4}. There is no value that never ends a wrap-up "
                + "(Timeout.InfiniteTimeSpan is rejected); zero closes each wrap-up as it opens.",
                member, TimeSpan.Zero, Max, (long)Max.TotalMilliseconds, duration),
            [member]);
    }
}

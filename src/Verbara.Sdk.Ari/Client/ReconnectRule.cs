using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Verbara.Sdk.Ari.Client;

/// <summary>
/// The reconnect values an <see cref="AriClient"/> can use when <see cref="AriClientOptions.AutoReconnect"/> is on:
/// exactly what <c>BackoffSchedule.Compute</c> accepts, plus no delay longer than .NET can wait.
/// </summary>
/// <remarks>
/// The rule: <see cref="AriClientOptions.ReconnectInitialDelay"/> from zero to <see cref="WaitLimit"/>;
/// <see cref="AriClientOptions.ReconnectMultiplier"/> finite and at least 1.0;
/// <see cref="AriClientOptions.ReconnectMaxDelay"/> from <see cref="AriClientOptions.ReconnectInitialDelay"/> to
/// <see cref="WaitLimit"/>; <see cref="AriClientOptions.MaxReconnectAttempts"/> zero (unlimited) or more. With
/// <see cref="AriClientOptions.AutoReconnect"/> off the backoff never runs and none of it is checked, the attempt
/// limit included (unlike Ami, whose attempt limit also carries an unconditional <see cref="RangeAttribute"/>). The validator reaches the rule through
/// <see cref="ReconnectRuleAttribute"/>; a constructor through <see cref="ThrowIfUnusable"/>. Ami's copy of this class
/// holds the same rule: the two packages do not share an internal type.
/// </remarks>
internal static class ReconnectRule
{
    /// <summary>The longest delay .NET can wait: <see cref="int.MaxValue"/> milliseconds, about 24.8 days.</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>The options the rule checks, in the order a constructor reports them.</summary>
    private static readonly string[] Members =
    [
        nameof(AriClientOptions.ReconnectInitialDelay),
        nameof(AriClientOptions.ReconnectMultiplier),
        nameof(AriClientOptions.ReconnectMaxDelay),
        nameof(AriClientOptions.MaxReconnectAttempts),
    ];

    /// <summary>
    /// Why the value of <paramref name="member"/> cannot be used by the reconnect backoff, or <see langword="null"/> when it
    /// can (or when <paramref name="member"/> is not one the rule checks). Does not look at
    /// <see cref="AriClientOptions.AutoReconnect"/>: callers check it first.
    /// </summary>
    internal static string? Violation(AriClientOptions options, string member) => member switch
    {
        nameof(AriClientOptions.ReconnectInitialDelay) => DelayViolation(member, options.ReconnectInitialDelay, TimeSpan.Zero, "zero"),
        nameof(AriClientOptions.ReconnectMaxDelay) => DelayViolation(member, options.ReconnectMaxDelay,
            options.ReconnectInitialDelay, nameof(AriClientOptions.ReconnectInitialDelay)),
        nameof(AriClientOptions.ReconnectMultiplier) when !double.IsFinite(options.ReconnectMultiplier) || options.ReconnectMultiplier < 1.0 =>
            Unusable(member, options.ReconnectMultiplier.ToString(CultureInfo.InvariantCulture), "a finite number of at least 1.0"),
        nameof(AriClientOptions.MaxReconnectAttempts) when options.MaxReconnectAttempts < 0 =>
            Unusable(member, options.MaxReconnectAttempts.ToString(CultureInfo.InvariantCulture), "0 (unlimited) or more"),
        _ => null,
    };

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> whose <see cref="ArgumentException.ParamName"/> is the first option
    /// the rule rejects, when <see cref="AriClientOptions.AutoReconnect"/> is on. No validator runs on a constructor's path.
    /// </summary>
    internal static void ThrowIfUnusable(AriClientOptions options)
    {
        if (!options.AutoReconnect)
            return;

        foreach (var member in Members)
        {
            if (Violation(options, member) is { } message)
                throw new ArgumentOutOfRangeException(member, message);
        }
    }

    private static string? DelayViolation(string member, TimeSpan value, TimeSpan floor, string floorName)
    {
        if (value < floor)
            return Unusable(member, value.ToString("c", CultureInfo.InvariantCulture), $"at least {floorName}");
        if (value > WaitLimit)
            return Unusable(member, value.ToString("c", CultureInfo.InvariantCulture),
                $"at most {WaitLimit.ToString("c", CultureInfo.InvariantCulture)} (int.MaxValue ms, the longest delay .NET can wait)");
        return null;
    }

    private static string Unusable(string member, string value, string accepted) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{member} = {value} cannot be used by the reconnect backoff while AutoReconnect is on: it must be {accepted}.");
}

/// <summary>
/// Puts <see cref="ReconnectRule"/> into the source-generated <see cref="AriClientOptionsValidator"/>: the generator
/// builds the <see cref="ValidationContext"/> over the options instance and sets <see cref="ValidationContext.MemberName"/>,
/// so the attribute can read <see cref="AriClientOptions.AutoReconnect"/> and the neighbouring delays. Internal: it
/// adds nothing to the public surface.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ReconnectRuleAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (validationContext.ObjectInstance is not AriClientOptions { AutoReconnect: true } options
            || validationContext.MemberName is not { } member)
            return ValidationResult.Success;

        return ReconnectRule.Violation(options, member) is { } message
            ? new ValidationResult(message, [member])
            : ValidationResult.Success;
    }
}

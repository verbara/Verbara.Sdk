using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// The <see cref="AmiConnectionOptions.ConnectionTimeout"/> an <see cref="AmiConnection"/> can use: more than zero and at
/// most <see cref="ReconnectRule.WaitLimit"/>, whether or not <see cref="AmiConnectionOptions.AutoReconnect"/> is on.
/// </summary>
/// <remarks>
/// The value bounds every connect, the caller's and the reconnect loop's, and the wait of a caller's connect for the
/// release of a session the connection lost on its own: zero, a negative value or <see cref="Timeout.InfiniteTimeSpan"/>
/// cannot bound either. The validator reaches the rule through <see cref="ConnectTimeoutRuleAttribute"/>; the constructor
/// through <see cref="ThrowIfUnusable"/>, before <see cref="ReconnectRule.ThrowIfUnusable"/>.
/// </remarks>
internal static class ConnectTimeoutRule
{
    /// <summary>The option the rule checks, as a constructor reports it.</summary>
    private static readonly string Member = nameof(AmiConnectionOptions.ConnectionTimeout);

    /// <summary>
    /// Why <see cref="AmiConnectionOptions.ConnectionTimeout"/> cannot be used, or <see langword="null"/> when it can.
    /// </summary>
    internal static string? Violation(AmiConnectionOptions options)
    {
        var value = options.ConnectionTimeout;
        if (value > TimeSpan.Zero && value <= ReconnectRule.WaitLimit)
            return null;

        var shown = value == Timeout.InfiniteTimeSpan
            ? "Timeout.InfiniteTimeSpan"
            : value.ToString("c", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Member} = {shown} cannot bound a connect: it must be more than zero and at most {ReconnectRule.WaitLimit.ToString("c", CultureInfo.InvariantCulture)} (int.MaxValue ms, the longest delay .NET can wait).");
    }

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> whose <see cref="ArgumentException.ParamName"/> is
    /// <c>ConnectionTimeout</c> when the rule rejects it. No validator runs on a constructor's path.
    /// </summary>
    internal static void ThrowIfUnusable(AmiConnectionOptions options)
    {
        if (Violation(options) is { } message)
            throw new ArgumentOutOfRangeException(Member, message);
    }
}

/// <summary>
/// Puts <see cref="ConnectTimeoutRule"/> into the source-generated <see cref="AmiConnectionOptionsValidator"/>, as
/// <see cref="ReconnectRuleAttribute"/> does for the backoff. Internal: it adds nothing to the public surface.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConnectTimeoutRuleAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (validationContext.ObjectInstance is not AmiConnectionOptions options)
            return ValidationResult.Success;

        return ConnectTimeoutRule.Violation(options) is { } message
            ? new ValidationResult(message, [nameof(AmiConnectionOptions.ConnectionTimeout)])
            : ValidationResult.Success;
    }
}

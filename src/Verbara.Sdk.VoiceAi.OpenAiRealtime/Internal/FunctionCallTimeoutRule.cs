using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;

/// <summary>
/// The <see cref="OpenAiRealtimeOptions.FunctionCallTimeout"/> the bridge can use: more than zero and at most
/// <see cref="int.MaxValue"/> milliseconds, the longest a timer can wait.
/// </summary>
/// <remarks>
/// Zero or a negative value would time out every call before it ran, and <see cref="Timeout.InfiniteTimeSpan"/> would
/// switch the bound off. The validator reaches the rule through <see cref="FunctionCallTimeoutRuleAttribute"/>; the
/// bridge through <see cref="ThrowIfUnusable"/>, in its constructor and again before each function call, because it
/// holds the options object by reference and no validator runs for <c>Options.Create</c>.
/// </remarks>
internal static class FunctionCallTimeoutRule
{
    /// <summary>The option the rule checks, as the bridge reports it.</summary>
    private static readonly string Member = nameof(OpenAiRealtimeOptions.FunctionCallTimeout);

    /// <summary>The longest accepted value: <see cref="int.MaxValue"/> milliseconds.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Why <paramref name="value"/> cannot bound a function call, or <see langword="null"/> when it can.</summary>
    internal static string? Violation(TimeSpan value)
    {
        if (value > TimeSpan.Zero && value <= Limit)
            return null;

        var shown = value == Timeout.InfiniteTimeSpan
            ? "Timeout.InfiniteTimeSpan"
            : value.ToString("c", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Member} = {shown} cannot bound a function call: it must be more than zero and at most {Limit.ToString("c", CultureInfo.InvariantCulture)} (int.MaxValue ms, the longest delay .NET can wait).");
    }

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> whose <see cref="ArgumentException.ParamName"/> is
    /// <c>FunctionCallTimeout</c> when the rule rejects <paramref name="options"/>' value; otherwise returns it.
    /// </summary>
    internal static TimeSpan ThrowIfUnusable(OpenAiRealtimeOptions options)
    {
        var value = options.FunctionCallTimeout;
        if (Violation(value) is { } message)
            throw new ArgumentOutOfRangeException(Member, message);
        return value;
    }
}

/// <summary>
/// Puts <see cref="FunctionCallTimeoutRule"/> into the source-generated <see cref="OpenAiRealtimeOptionsValidator"/>.
/// Internal: it adds nothing to the public surface.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class FunctionCallTimeoutRuleAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not TimeSpan timeout)
            return ValidationResult.Success;

        return FunctionCallTimeoutRule.Violation(timeout) is { } message
            ? new ValidationResult(message, [nameof(OpenAiRealtimeOptions.FunctionCallTimeout)])
            : ValidationResult.Success;
    }
}

using System.Globalization;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// The one rule for <see cref="WebhookDeliveryOptions"/> values webhook delivery can use, shared by the options
/// validator (<see cref="WebhookDeliveryOptionsValidator"/>) and both <see cref="WebhookDeliveryService"/>
/// constructors. Every rejected value is one the delivery loop can never honour: a retry delay
/// <c>BackoffSchedule.Compute</c> or <c>Task.Delay</c> throws on, a negative retry count, or a per-attempt
/// timeout that ends every attempt before it starts or that <c>HttpClient.Timeout</c> refuses (above <c>int.MaxValue</c>
/// milliseconds), so every attempt fails.
/// </summary>
internal static class WebhookDeliveryRule
{
    /// <summary>
    /// The longest delay or finite per-attempt timeout accepted: <c>int.MaxValue</c> milliseconds, the bound the AMI/ARI
    /// reconnect rule uses and the largest finite value <c>HttpClient.Timeout</c> takes.
    /// </summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>The checked members, in the order the constructors report the first violation.</summary>
    internal static readonly string[] Members =
    [
        nameof(WebhookDeliveryOptions.MaxRetries),
        nameof(WebhookDeliveryOptions.InitialDelay),
        nameof(WebhookDeliveryOptions.MaxDelay),
        nameof(WebhookDeliveryOptions.TimeoutPerAttempt),
    ];

    /// <summary>The message naming <paramref name="member"/> when its value is unusable; <see langword="null"/> when usable.</summary>
    internal static string? Violation(WebhookDeliveryOptions options, string member) => member switch
    {
        nameof(WebhookDeliveryOptions.MaxRetries) when options.MaxRetries < 0 =>
            Unusable(member, options.MaxRetries.ToString(CultureInfo.InvariantCulture), "0 or more"),
        nameof(WebhookDeliveryOptions.InitialDelay) =>
            DelayViolation(member, options.InitialDelay, TimeSpan.Zero, "zero"),
        nameof(WebhookDeliveryOptions.MaxDelay) =>
            DelayViolation(member, options.MaxDelay, options.InitialDelay, nameof(WebhookDeliveryOptions.InitialDelay)),
        nameof(WebhookDeliveryOptions.TimeoutPerAttempt)
            when options.TimeoutPerAttempt != Timeout.InfiniteTimeSpan
                && (options.TimeoutPerAttempt <= TimeSpan.Zero || options.TimeoutPerAttempt > WaitLimit) =>
            Unusable(member, Format(options.TimeoutPerAttempt),
                "greater than zero and at most int.MaxValue ms, or Timeout.InfiniteTimeSpan"),
        _ => null,
    };

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> whose <c>ParamName</c> is the first unusable member.</summary>
    internal static void ThrowIfUnusable(WebhookDeliveryOptions options)
    {
        foreach (var member in Members)
        {
            if (Violation(options, member) is { } message)
                throw new ArgumentOutOfRangeException(member, message);
        }
    }

    private static string? DelayViolation(string member, TimeSpan value, TimeSpan floor, string floorName)
    {
        if (value < floor)
            return Unusable(member, Format(value), $"at least {floorName}");
        if (value > WaitLimit)
            return Unusable(member, Format(value), "at most int.MaxValue milliseconds");
        return null;
    }

    private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    private static string Unusable(string member, string value, string accepted) =>
        string.Create(CultureInfo.InvariantCulture, $"{member} = {value} cannot be used by webhook delivery: it must be {accepted}.");
}

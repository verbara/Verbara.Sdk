namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Tunable delivery policy for <see cref="WebhookDeliveryService"/>. Retries use exponential
/// backoff capped at <see cref="MaxDelay"/>.
/// </summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>Maximum delivery attempts per event per subscription. Default 5.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Initial retry delay. Doubles on each failure up to <see cref="MaxDelay"/>. Default 1 s.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on retry delay. Default 60 s.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>HTTP timeout for a single attempt. Default 10 s.</summary>
    public TimeSpan TimeoutPerAttempt { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Value of the <c>User-Agent</c> header sent with every delivery.</summary>
    public string UserAgent { get; set; } = "Asterisk.Sdk.Push.Webhooks/1.0";
}

namespace Asterisk.Sdk.Resilience;

/// <summary>
/// Stateless backoff delay calculator for reconnect loops and iterative retry schedules
/// that don't fit the bounded <see cref="ResiliencePolicy.ExecuteAsync"/> model — e.g.,
/// continuous state loops (AMI/ARI reconnect) where retries are unbounded and driven by
/// external state transitions rather than exception count.
/// </summary>
/// <remarks>
/// Use <see cref="ResiliencePolicy"/> when the retry boundary is "up to N attempts around
/// a single fallible operation". Use <see cref="BackoffSchedule"/> when the retry boundary
/// is "a loop that continues until a state flag changes" and you just need to compute the
/// next delay from the current attempt number.
/// </remarks>
public static class BackoffSchedule
{
    /// <summary>
    /// Returns the backoff delay for the given 1-based <paramref name="attempt"/>.
    /// Formula: <c>min(baseDelay * multiplier^(attempt-1), maxDelay)</c>.
    /// </summary>
    /// <param name="attempt">1-based attempt number. Must be &gt;= 1.</param>
    /// <param name="baseDelay">The initial delay (used on attempt 1).</param>
    /// <param name="multiplier">Factor applied per attempt. Typical values: 1.5, 2.0, 2.5. Must be &gt;= 1.0.</param>
    /// <param name="maxDelay">Upper bound on the returned delay. Must be &gt;= <paramref name="baseDelay"/>.</param>
    /// <returns>The computed delay, capped at <paramref name="maxDelay"/>. Never negative.</returns>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, double multiplier, TimeSpan maxDelay)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt must be >= 1.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "Base delay cannot be negative.");
        if (multiplier < 1.0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Multiplier must be >= 1.0 and finite.");
        if (maxDelay < baseDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), maxDelay, "Max delay must be >= base delay.");

        // Cap shift to avoid overflow: multiplier^30 >> 1_000_000 for multiplier=2.
        // For attempt > 100 (extremely rare) we'd otherwise multiply a very large number.
        var exponent = Math.Min(attempt - 1, 100);
        var scaled = baseDelay.TotalMilliseconds * Math.Pow(multiplier, exponent);

        // Math.Pow may return Infinity for very large exponents with multiplier > 1; guard.
        if (double.IsInfinity(scaled) || scaled > maxDelay.TotalMilliseconds)
            return maxDelay;

        return TimeSpan.FromMilliseconds(scaled);
    }

    /// <summary>
    /// Returns the backoff delay for the given attempt with deterministic ±<paramref name="jitterFraction"/>
    /// jitter applied via the provided <paramref name="jitter"/> source.
    /// </summary>
    /// <param name="attempt">1-based attempt number.</param>
    /// <param name="baseDelay">Initial delay.</param>
    /// <param name="multiplier">Backoff multiplier.</param>
    /// <param name="maxDelay">Upper bound (applied AFTER jitter).</param>
    /// <param name="jitterFraction">Jitter range as a fraction (e.g. 0.2 = ±10% around the base). 0 = no jitter.</param>
    /// <param name="jitter"><see cref="Random"/> source for jitter; caller's responsibility to seed for determinism.</param>
    public static TimeSpan ComputeWithJitter(
        int attempt,
        TimeSpan baseDelay,
        double multiplier,
        TimeSpan maxDelay,
        double jitterFraction,
        Random jitter)
    {
        ArgumentNullException.ThrowIfNull(jitter);
        if (jitterFraction < 0.0 || jitterFraction > 1.0 || double.IsNaN(jitterFraction))
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), jitterFraction, "Jitter fraction must be in [0.0, 1.0].");

        var baseValue = Compute(attempt, baseDelay, multiplier, maxDelay);
        if (jitterFraction <= 0.0)
            return baseValue;

        // Symmetric jitter: ±(jitterFraction/2) around baseValue.
        var factor = 1.0 + ((jitter.NextDouble() - 0.5) * jitterFraction);
        var jittered = baseValue.TotalMilliseconds * factor;

        // Cap at maxDelay, floor at 0.
        if (jittered <= 0)
            return TimeSpan.Zero;
        if (jittered > maxDelay.TotalMilliseconds)
            return maxDelay;
        return TimeSpan.FromMilliseconds(jittered);
    }
}

namespace Asterisk.Sdk.Resilience;

/// <summary>
/// Fluent builder for <see cref="ResiliencePolicy"/>. Compose the three primitives
/// (timeout, retry, circuit breaker) by chaining With* calls, then terminate with
/// <see cref="Build"/>. Each <c>With*</c> returns the same builder instance for
/// further configuration.
/// </summary>
/// <remarks>
/// The builder is mutable during configuration; the resulting <see cref="ResiliencePolicy"/>
/// is effectively immutable (its only per-key mutable state lives in the circuit-state
/// dictionary). <see cref="TimeProvider"/> defaults to <see cref="TimeProvider.System"/>
/// when <see cref="WithTimeProvider"/> is not invoked.
/// </remarks>
public sealed class ResiliencePolicyBuilder
{
    /// <summary>Upper bound enforced on <see cref="WithRetry"/> <c>maxAttempts</c>.</summary>
    public const int MaxRetryAttemptsCap = 10;

    internal int? CircuitThreshold;
    internal TimeSpan CircuitOpenDuration;
    internal int? RetryMaxAttempts;
    internal TimeSpan RetryBaseDelay;
    internal TimeSpan? Timeout;
    internal TimeProvider TimeProviderInstance = TimeProvider.System;

    /// <summary>
    /// Configures a per-key circuit breaker. Opens after <paramref name="threshold"/>
    /// consecutive failures and stays open for <paramref name="openDuration"/> before
    /// admitting a half-open probe.
    /// </summary>
    public ResiliencePolicyBuilder WithCircuitBreaker(int threshold, TimeSpan openDuration)
    {
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be >= 1.");
        if (openDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(openDuration), openDuration, "Open duration must be > 0.");

        CircuitThreshold = threshold;
        CircuitOpenDuration = openDuration;
        return this;
    }

    /// <summary>
    /// Configures a retry with exponential backoff. <paramref name="maxAttempts"/> counts
    /// the total attempts including the first one (so <c>maxAttempts == 3</c> means
    /// 1 initial + 2 retries). Values greater than <see cref="MaxRetryAttemptsCap"/>
    /// are clamped to that cap as a sanity limit. Delays follow
    /// <c>baseDelay * 2^(attempt-1)</c> with ±20% deterministic jitter.
    /// </summary>
    public ResiliencePolicyBuilder WithRetry(int maxAttempts, TimeSpan baseDelay)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Max attempts must be >= 1.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "Base delay cannot be negative.");

        RetryMaxAttempts = Math.Min(maxAttempts, MaxRetryAttemptsCap);
        RetryBaseDelay = baseDelay;
        return this;
    }

    /// <summary>
    /// Configures a per-attempt timeout. When the wrapped action takes longer than
    /// <paramref name="timeout"/> a <see cref="TimeoutException"/> is thrown (and
    /// counted against retries if retry is also configured).
    /// </summary>
    public ResiliencePolicyBuilder WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be > 0.");

        Timeout = timeout;
        return this;
    }

    /// <summary>Overrides the clock used for circuit state + backoff delay scheduling.</summary>
    public ResiliencePolicyBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProviderInstance = timeProvider;
        return this;
    }

    /// <summary>Builds the immutable <see cref="ResiliencePolicy"/>.</summary>
    public ResiliencePolicy Build() => new(this);
}

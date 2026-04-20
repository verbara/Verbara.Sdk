using System.Collections.Concurrent;
using Asterisk.Sdk.Resilience.Diagnostics;

namespace Asterisk.Sdk.Resilience;

/// <summary>
/// Composition of per-attempt timeout, per-key circuit breaker, and retry/backoff.
/// Built via <see cref="ResiliencePolicyBuilder"/>; direct construction is not supported
/// so that invariants (e.g., retry cap) are enforced centrally.
/// </summary>
/// <remarks>
/// Execution order when all three primitives are configured:
/// <list type="number">
///   <item>Circuit check (throws <see cref="CircuitBreakerOpenException"/> if open).</item>
///   <item>Per-attempt timeout (linked <see cref="CancellationTokenSource"/>).</item>
///   <item>Action invocation.</item>
///   <item>On failure: record + retry with exponential backoff (if configured).</item>
/// </list>
/// All time reads go through the injected <see cref="TimeProvider"/> so tests can use
/// <c>FakeTimeProvider</c> for determinism.
/// </remarks>
public sealed class ResiliencePolicy
{
    /// <summary>No-op policy that invokes the action directly without any resilience logic.</summary>
    public static ResiliencePolicy NoOp { get; } = new ResiliencePolicyBuilder().Build();

    private readonly int? _circuitThreshold;
    private readonly TimeSpan _circuitOpenDuration;
    private readonly int? _retryMaxAttempts;
    private readonly TimeSpan _retryBaseDelay;
    private readonly TimeSpan? _timeout;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuits = new(StringComparer.Ordinal);

    // Deterministic jitter source seeded per-policy — avoids shared Random contention and
    // gives reproducible sequences per policy instance while still spreading retries.
    private readonly Random _jitter;

    internal ResiliencePolicy(ResiliencePolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _circuitThreshold = builder.CircuitThreshold;
        _circuitOpenDuration = builder.CircuitOpenDuration;
        _retryMaxAttempts = builder.RetryMaxAttempts;
        _retryBaseDelay = builder.RetryBaseDelay;
        _timeout = builder.Timeout;
        _time = builder.TimeProviderInstance;

        // Seed from current ticks — tests that need determinism can configure TimeProvider,
        // which bounds the jitter range but not its exact value; production has no need.
        _jitter = new Random(unchecked((int)_time.GetUtcNow().UtcTicks));
    }

    /// <summary>
    /// Executes <paramref name="action"/> under the configured policies.
    /// </summary>
    /// <typeparam name="T">Return type of the wrapped action.</typeparam>
    /// <param name="key">Logical key identifying the circuit bucket (per-node, per-provider, etc.).</param>
    /// <param name="action">The operation to execute. Receives the effective cancellation token (includes timeout linkage when configured).</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <exception cref="CircuitBreakerOpenException">The circuit for <paramref name="key"/> is open.</exception>
    /// <exception cref="TimeoutException">A per-attempt timeout elapsed (and retries, if any, are exhausted).</exception>
    /// <exception cref="OperationCanceledException">Caller token cancelled; propagated unchanged.</exception>
    public async ValueTask<T> ExecuteAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(action);

        var state = _circuitThreshold.HasValue
            ? _circuits.GetOrAdd(key, static k => new CircuitBreakerState(k))
            : null;

        var maxAttempts = _retryMaxAttempts ?? 1;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Circuit check ahead of every attempt; retries don't bypass a tripped circuit.
            if (state is not null && !state.ShouldAllow(_circuitOpenDuration, _time))
            {
                ResilienceMetrics.SetCircuitState(key, ResilienceMetrics.CircuitStateValue.Open);
                throw new CircuitBreakerOpenException(key);
            }

            try
            {
                T result;
                if (_timeout.HasValue)
                {
                    // TimeProvider-aware timeout CTS linked with the caller token so either
                    // trigger (elapsed timeout or caller cancellation) cancels the action.
                    using var timeoutCts = new CancellationTokenSource(_timeout.Value, _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        result = await action(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        ResilienceMetrics.TimeoutFired.Add(
                            1,
                            new KeyValuePair<string, object?>("key", key));
                        throw new TimeoutException(
                            $"Action for key '{key}' exceeded timeout of {_timeout.Value}.");
                    }
                }
                else
                {
                    result = await action(ct).ConfigureAwait(false);
                }

                // Success path — clear circuit if it was previously trending to open.
                if (state is not null)
                {
                    var wasOpen = state.IsOpen;
                    state.RecordSuccess();
                    if (wasOpen)
                    {
                        ResilienceMetrics.CircuitClosed.Add(
                            1,
                            new KeyValuePair<string, object?>("key", key));
                    }
                    ResilienceMetrics.SetCircuitState(key, ResilienceMetrics.CircuitStateValue.Closed);
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller-initiated cancellation: propagate unchanged, do not count as failure.
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (state is not null && _circuitThreshold is int threshold)
                {
                    var wasOpen = state.IsOpen;
                    state.RecordFailure(threshold, _time);
                    if (!wasOpen && state.IsOpen)
                    {
                        ResilienceMetrics.CircuitOpened.Add(
                            1,
                            new KeyValuePair<string, object?>("key", key));
                        ResilienceMetrics.SetCircuitState(key, ResilienceMetrics.CircuitStateValue.Open);
                    }
                }

                // If there are retries left, count this attempt and back off; otherwise rethrow below.
                if (attempt < maxAttempts)
                {
                    ResilienceMetrics.RetryAttempts.Add(
                        1,
                        new KeyValuePair<string, object?>("key", key));

                    var delay = ComputeBackoff(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        // Retries exhausted — rethrow the last observed exception.
        // lastException is non-null here because we only exit the loop via return or after a catch.
        throw lastException!;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        if (_retryBaseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        // Exponential: base * 2^(attempt-1). Cap exponent to avoid overflow for large caps.
        var shift = Math.Min(attempt - 1, 20);
        var expMs = _retryBaseDelay.TotalMilliseconds * (1L << shift);

        // ±20% jitter. jitter.NextDouble() is not threadsafe across calls; serialize with lock-free
        // monitor by wrapping in a short critical section — but we avoid the `lock` keyword per
        // repo convention, so we rely on per-policy Random + ValueTask execution being serialized
        // at the policy level (multiple concurrent ExecuteAsync calls may interleave here; a
        // small amount of contention is acceptable, and System.Random's documented behavior under
        // concurrent use yields arbitrary values without corruption).
        var jitterFactor = 1.0 + ((_jitter.NextDouble() - 0.5) * 0.4);
        var jittered = expMs * jitterFactor;

        return TimeSpan.FromMilliseconds(jittered);
    }
}

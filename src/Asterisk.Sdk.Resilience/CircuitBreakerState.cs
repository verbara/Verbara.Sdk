namespace Asterisk.Sdk.Resilience;

/// <summary>
/// Per-key circuit breaker state. Thread-safe via <see cref="Interlocked"/> primitives
/// and <see cref="Volatile"/> reads/writes — no <c>lock</c>s are taken.
/// </summary>
/// <remarks>
/// State transitions follow the classic closed → open → half-open → closed cycle:
/// <list type="bullet">
///   <item><c>RecordFailure</c> increments the failure counter; when the threshold is
///     reached for the first time, the circuit opens and <see cref="OpenedAt"/> is
///     set from the injected <see cref="TimeProvider"/>.</item>
///   <item><c>ShouldAllow</c> returns <see langword="true"/> when the circuit is closed
///     or when the open duration has elapsed (half-open probe — state is reset so the
///     caller can attempt the operation).</item>
///   <item><c>RecordSuccess</c> resets the failure counter and closes the circuit.</item>
/// </list>
/// </remarks>
public sealed class CircuitBreakerState
{
    // Atomic fields. _isOpen stored as int (0/1) so Interlocked.Exchange works on it.
    private int _consecutiveFailures;
    private int _isOpen;

    // OpenedAt stored as long ticks (UTC), with long.MinValue sentinel meaning "null".
    // Interlocked.Read/Exchange on 64-bit values is atomic.
    private const long NullTicksSentinel = long.MinValue;
    private long _openedAtTicks = NullTicksSentinel;

    /// <summary>
    /// Creates a new circuit state for the given logical key (node id, provider name, etc.).
    /// </summary>
    /// <param name="key">Non-null key used for metrics tagging and log context.</param>
    public CircuitBreakerState(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
    }

    /// <summary>The logical key this state represents.</summary>
    public string Key { get; }

    /// <summary>Current consecutive failure count (atomic read).</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>Whether the circuit is currently in the open state.</summary>
    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    /// <summary>The <see cref="TimeProvider"/>-based timestamp when the circuit was last opened.</summary>
    public DateTimeOffset? OpenedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _openedAtTicks);
            return ticks == NullTicksSentinel
                ? null
                : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the circuit is closed OR the open duration has
    /// elapsed (in which case the state is auto-reset to half-open by closing the circuit
    /// and clearing failures, so the caller may probe). Returns <see langword="false"/>
    /// only while the circuit is open and still within <paramref name="openDuration"/>.
    /// </summary>
    public bool ShouldAllow(TimeSpan openDuration, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (Volatile.Read(ref _isOpen) == 0)
            return true;

        var openedAtTicks = Interlocked.Read(ref _openedAtTicks);
        if (openedAtTicks == NullTicksSentinel)
            return true;

        var openedAt = new DateTimeOffset(openedAtTicks, TimeSpan.Zero);
        var elapsed = time.GetUtcNow() - openedAt;
        if (elapsed >= openDuration)
        {
            // Auto-transition to half-open: clear state so the caller can probe.
            // Idempotent under concurrency — multiple threads may race here but the
            // outcome (closed state + 0 failures) is the same.
            Interlocked.Exchange(ref _openedAtTicks, NullTicksSentinel);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _isOpen, 0);
            return true;
        }

        return false;
    }

    /// <summary>Resets the failure counter and closes the circuit.</summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _isOpen, 0);
        Interlocked.Exchange(ref _openedAtTicks, NullTicksSentinel);
    }

    /// <summary>
    /// Records a failure. If the cumulative failure count meets or exceeds
    /// <paramref name="threshold"/> and the circuit is not already open, transitions
    /// to the open state and stores <see cref="OpenedAt"/> via the provided clock.
    /// </summary>
    public void RecordFailure(int threshold, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be >= 1.");

        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures < threshold)
            return;

        // Try to flip from closed (0) → open (1). Only the thread that wins records OpenedAt —
        // preventing a spurious re-open from overwriting the original timestamp.
        if (Interlocked.CompareExchange(ref _isOpen, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _openedAtTicks, time.GetUtcNow().UtcTicks);
        }
    }
}

using Verbara.Sdk.Resilience.Diagnostics;

namespace Verbara.Sdk.Resilience;

/// <summary>
/// Per-key circuit breaker state. Thread-safe via <see cref="Interlocked"/> primitives
/// and <see cref="Volatile"/> reads/writes — no <c>lock</c>s are taken.
/// </summary>
/// <remarks>
/// State transitions follow the classic closed → open → half-open → closed cycle:
/// <list type="bullet">
///   <item><c>RecordFailure</c> increments the failure counter; when the threshold is
///     reached, a closed circuit opens and <see cref="OpenedAt"/> is set from the injected
///     <see cref="TimeProvider"/>.</item>
///   <item>Once the open window has elapsed, the first <c>ShouldAllow</c> returns <see langword="true"/>
///     and takes the circuit's one probe: the circuit is half-open, and every other <c>ShouldAllow</c>
///     returns <see langword="false"/> until a result is recorded or a further open window has passed
///     since the probe was admitted.</item>
///   <item>A failure recorded while half-open re-opens the circuit at once, whatever the threshold,
///     for a full open window from that failure.</item>
///   <item><c>RecordSuccess</c> resets the failure counter and closes the circuit, from open or half-open.</item>
/// </list>
/// A result does not carry the identity of the call that was admitted: whichever failure or success is
/// recorded while the circuit is half-open decides it.
/// </remarks>
public sealed class CircuitBreakerState
{
    // The phase is one int, so a transition is one atomic write or compare-and-swap.
    private const int Closed = 0;
    private const int Open = 1;
    private const int HalfOpen = 2;

    private int _consecutiveFailures;
    private int _phase;

    // Ticks (UTC) with long.MinValue as "none". Interlocked.Read/Exchange on 64-bit values is atomic.
    // _openedAtTicks is the last opening; _probeAtTicks is when the half-open probe was admitted.
    private const long NullTicksSentinel = long.MinValue;
    private long _openedAtTicks = NullTicksSentinel;
    private long _probeAtTicks = NullTicksSentinel;

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

    /// <summary>Current consecutive failure count (atomic read). Only a success clears it.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Whether the circuit is open. A half-open circuit, whose probe has been admitted, is not open, so a caller
    /// comparing this around <see cref="RecordFailure"/> sees a failed probe as an opening.
    /// </summary>
    public bool IsOpen => Volatile.Read(ref _phase) == Open;

    /// <summary>
    /// The <see cref="TimeProvider"/>-based timestamp when the circuit was last opened. A half-open circuit keeps
    /// it; a success clears it.
    /// </summary>
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
    /// Admits a call: returns <see langword="true"/> when the circuit is closed, and, once the open window has
    /// elapsed, to exactly one caller, whose call becomes the circuit's probe. A <see langword="true"/> past the
    /// window is not a question: it takes the probe, and every other call is rejected until that call's result
    /// is recorded through <see cref="RecordSuccess"/> or <see cref="RecordFailure"/>, or until a further
    /// <paramref name="openDuration"/> has passed since the probe was admitted. A caller that records nothing
    /// therefore leaves the circuit rejecting for up to one more window. Returns <see langword="false"/> while
    /// the circuit is open within <paramref name="openDuration"/> and while a probe holds the slot.
    /// </summary>
    public bool ShouldAllow(TimeSpan openDuration, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        var phase = Volatile.Read(ref _phase);
        if (phase == Closed)
            return true;

        if (phase == Open)
            return TryClaimProbe(openDuration, time);

        return TryTakeExpiredProbe(openDuration, time);
    }

    // Open → half-open, by one winner. The clock is read before the swap: a clock that throws leaves the circuit
    // open, never half-open with no probe time, which no expiry could reach.
    private bool TryClaimProbe(TimeSpan openDuration, TimeProvider time)
    {
        var openedAtTicks = Interlocked.Read(ref _openedAtTicks);
        if (openedAtTicks == NullTicksSentinel)
        {
            // A trip or a success is between its writes; nothing to claim, and the admission is what it always was.
            return true;
        }

        var nowTicks = time.GetUtcNow().UtcTicks;
        if (!WindowHasElapsed(openedAtTicks, openDuration, nowTicks))
            return false;

        if (Interlocked.CompareExchange(ref _phase, HalfOpen, Open) != Open)
            return false;

        // A failed probe may have re-opened the circuit after the opening time above was read: that newer window
        // has not elapsed, so hand it back. A result recorded meanwhile has already moved the phase, and wins.
        if (Interlocked.Read(ref _openedAtTicks) != openedAtTicks)
        {
            Interlocked.CompareExchange(ref _phase, Open, HalfOpen);
            return false;
        }

        Interlocked.Exchange(ref _probeAtTicks, nowTicks);
        return true;
    }

    // A probe that has reported nothing for a whole window hands its slot on, to one winner.
    private bool TryTakeExpiredProbe(TimeSpan openDuration, TimeProvider time)
    {
        var probeAtTicks = Interlocked.Read(ref _probeAtTicks);
        if (probeAtTicks == NullTicksSentinel)
            return false;

        var nowTicks = time.GetUtcNow().UtcTicks;
        if (!WindowHasElapsed(probeAtTicks, openDuration, nowTicks))
            return false;

        return Interlocked.CompareExchange(ref _probeAtTicks, nowTicks, probeAtTicks) == probeAtTicks;
    }

    /// <summary>
    /// Reads the state of the circuit now, without changing it: <see cref="ResilienceMetrics.CircuitStateValue.Closed"/>
    /// when it is closed, <see cref="ResilienceMetrics.CircuitStateValue.Open"/> while it is open within its window, and
    /// <see cref="ResilienceMetrics.CircuitStateValue.HalfOpen"/> from the end of that window until a probe's result is
    /// recorded, for the whole time a probe is in flight.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ShouldAllow"/> admits and takes the probe. This read never does, so any number of reads leaves
    /// the failure count, <see cref="IsOpen"/>, <see cref="OpenedAt"/> and the admission of the next call as they were.
    /// </remarks>
    internal ResilienceMetrics.CircuitStateValue ReadState(TimeSpan openDuration, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        var phase = Volatile.Read(ref _phase);
        if (phase == Closed)
            return ResilienceMetrics.CircuitStateValue.Closed;

        if (phase == HalfOpen)
            return ResilienceMetrics.CircuitStateValue.HalfOpen;

        var openedAtTicks = Interlocked.Read(ref _openedAtTicks);
        if (openedAtTicks == NullTicksSentinel
            || WindowHasElapsed(openedAtTicks, openDuration, time.GetUtcNow().UtcTicks))
        {
            return ResilienceMetrics.CircuitStateValue.HalfOpen;
        }

        return ResilienceMetrics.CircuitStateValue.Open;
    }

    // The one window test: ShouldAllow and ReadState must agree at the boundary.
    private static bool WindowHasElapsed(long sinceTicks, TimeSpan openDuration, long nowTicks) =>
        nowTicks - sinceTicks >= openDuration.Ticks;

    // Moves the opening time forward to a failed probe's sample, never back: two failures racing on a half-open circuit
    // leave the later sample, so the one that loses after a stall cannot shorten the window the other opened. A time a
    // success has cleared stays cleared.
    private void RaiseOpenedAt(long ticks)
    {
        var current = Interlocked.Read(ref _openedAtTicks);
        while (current != NullTicksSentinel && current < ticks)
        {
            var seen = Interlocked.CompareExchange(ref _openedAtTicks, ticks, current);
            if (seen == current)
                return;
            current = seen;
        }
    }

    /// <summary>Resets the failure counter and closes the circuit, from open or half-open.</summary>
    public void RecordSuccess() => RecordSuccessReportingClose();

    /// <summary>
    /// Does what <see cref="RecordSuccess"/> does, and returns <see langword="true"/> only for the one call that
    /// moved the circuit out of open or half-open, so that a close can be counted once.
    /// </summary>
    internal bool RecordSuccessReportingClose()
    {
        // The phase is written last, so an opening time is never erased after a trip it belongs to.
        // Sequential tests cannot see this order: keep it.
        Interlocked.Exchange(ref _openedAtTicks, NullTicksSentinel);
        Interlocked.Exchange(ref _probeAtTicks, NullTicksSentinel);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return Interlocked.Exchange(ref _phase, Closed) != Closed;
    }

    /// <summary>
    /// Records a failure. A failure recorded while the circuit is half-open re-opens it at once, whatever
    /// <paramref name="threshold"/>, and stores <see cref="OpenedAt"/> via the provided clock. Otherwise, if the
    /// cumulative failure count meets or exceeds <paramref name="threshold"/> and the circuit is closed, it opens
    /// and stores <see cref="OpenedAt"/>.
    /// </summary>
    public void RecordFailure(int threshold, TimeProvider time) => RecordFailureReportingTrip(threshold, time);

    /// <summary>
    /// Does what <see cref="RecordFailure"/> does, and returns <see langword="true"/> only for the one call
    /// that moved the circuit to open, from closed or from half-open, so that an opening is counted once
    /// however many failures land together. A failure on a circuit that is already open returns
    /// <see langword="false"/>.
    /// </summary>
    /// <param name="threshold">Consecutive failures that open a closed circuit; at least 1.</param>
    /// <param name="time">The clock the opening time is read from.</param>
    /// <returns><see langword="true"/> when this failure opened the circuit.</returns>
    public bool RecordFailureReportingTrip(int threshold, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be >= 1.");

        var failures = Interlocked.Increment(ref _consecutiveFailures);

        while (Volatile.Read(ref _phase) == HalfOpen)
        {
            // The probe time names the half-open phase this failure acts on, and the clock is read after it, so the
            // sample is never older than that phase. Read the clock before anything changes: a clock that throws here
            // leaves the circuit as it was.
            var probeAtTicks = Interlocked.Read(ref _probeAtTicks);
            var reopenedAtTicks = time.GetUtcNow().UtcTicks;

            if (probeAtTicks != NullTicksSentinel)
            {
                // Clear the probe time from the value read, so only one failure re-opens this phase. A failure whose
                // read is out of date, after a re-open, a new claim or a hand-over, loses here and reads again.
                if (Interlocked.CompareExchange(ref _probeAtTicks, NullTicksSentinel, probeAtTicks) != probeAtTicks)
                    continue;
            }

            // The new opening time first, so open is never seen with the old, elapsed one; then clear the probe
            // time, so the next claim is the only one; then the phase. With no probe time read, a claim is between
            // its swap and its probe-time write, or another result is moving the circuit.
            RaiseOpenedAt(reopenedAtTicks);
            if (probeAtTicks == NullTicksSentinel)
                Interlocked.Exchange(ref _probeAtTicks, NullTicksSentinel);
            if (Interlocked.CompareExchange(ref _phase, Open, HalfOpen) == HalfOpen)
                return true;

            // A success or another failure moved the circuit first: this failure follows the closed rule.
            break;
        }

        if (failures < threshold)
            return false;

        // Read the clock before anything changes: a clock that throws here leaves the circuit as it was.
        var openedAtTicks = time.GetUtcNow().UtcTicks;

        // Try to flip from closed → open. Only the thread that wins records OpenedAt —
        // preventing a spurious re-open from overwriting the original timestamp.
        if (Interlocked.CompareExchange(ref _phase, Open, Closed) != Closed)
            return false;

        Interlocked.Exchange(ref _openedAtTicks, openedAtTicks);
        return true;
    }
}

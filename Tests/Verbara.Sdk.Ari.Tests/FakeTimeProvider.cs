using System.Threading.Channels;

namespace Verbara.Sdk.Ari.Tests;

/// <summary>
/// Test double for <see cref="TimeProvider"/> whose clock moves only on <see cref="Advance"/>, and
/// whose one-shot timers fire only when it does.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from <c>Tests/Verbara.Sdk.VoiceAi.AudioSocket.Tests/FakeTimeProvider.cs</c>, which is
/// private to that project. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// waits through <see cref="CreateTimer"/>, which is how the accept backoff in
/// <c>AriOutboundListener.AcceptLoopAsync</c> is driven without waiting out a real one.
/// </para>
/// <para>
/// Every timer is also published on <see cref="TimersCreated"/> as it is created. That is how a test
/// reads the delay the code under test asked for without waiting any of it, and how it knows the
/// code is parked on that delay before it moves the clock.
/// </para>
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _scheduled = [];
    private readonly Channel<FakeTimer> _created = Channel.CreateUnbounded<FakeTimer>();
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Every timer created through this provider, in creation order.</summary>
    public ChannelReader<FakeTimer> TimersCreated => _created.Reader;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfPeriodic(period);

        var timer = new FakeTimer(this, callback, state, dueTime);
        Schedule(timer, dueTime);
        _created.Writer.TryWrite(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that has come due, in due order.
    /// </summary>
    /// <remarks>
    /// Callbacks run on the calling thread, outside the lock: a callback commonly resumes the code
    /// under test, which may create its next timer before this method returns.
    /// </remarks>
    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now = _now.Add(delta);
            due = [.. _scheduled.Where(t => t.DueAt <= _now).OrderBy(t => t.DueAt)];
            foreach (var timer in due)
            {
                _scheduled.Remove(timer);
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private void Schedule(FakeTimer timer, TimeSpan dueTime)
    {
        lock (_gate)
        {
            _scheduled.Remove(timer);
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _now.Add(dueTime);
            if (timer.DueAt is not null)
                _scheduled.Add(timer);
        }
    }

    private void Unschedule(FakeTimer timer)
    {
        lock (_gate)
        {
            _scheduled.Remove(timer);
            timer.DueAt = null;
        }
    }

    private static void ThrowIfPeriodic(TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
            throw new NotSupportedException("Only one-shot timers are faked; nothing under test asks for a period.");
    }

    /// <summary>A one-shot timer on the fake clock.</summary>
    internal sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        internal FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            DueTime = dueTime;
        }

        /// <summary>The due time the timer was created with: the delay its creator asked for.</summary>
        public TimeSpan DueTime { get; }

        /// <summary>
        /// When the timer fires on the fake clock, or <see langword="null"/> once it has fired or been
        /// disposed. Read and written under the owner's lock only.
        /// </summary>
        internal DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ThrowIfPeriodic(period);
            _owner.Schedule(this, dueTime);
            return true;
        }

        public void Dispose() => _owner.Unschedule(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void Fire() => _callback(_state);
    }
}

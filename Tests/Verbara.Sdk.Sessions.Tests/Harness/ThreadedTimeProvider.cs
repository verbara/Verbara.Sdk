using System.Collections.Concurrent;

namespace Verbara.Sdk.Sessions.Tests.Harness;

/// <summary>
/// Test double for <see cref="TimeProvider"/> whose clock moves only on <see cref="Advance"/>, and whose one-shot
/// timers, once due, run their callback on a thread-pool thread, as <see cref="System.Threading.Timer"/> does. It is
/// the threaded counterpart of <see cref="FakeTimeProvider"/>, for the races a timer callback can run into: a
/// callback that is still running when its owner is disposed, and one that runs after its timer was replaced.
/// </summary>
/// <remarks>
/// <para>
/// It keeps the two properties of the system timer those races depend on. <see cref="ITimer.Dispose"/> unschedules
/// the timer and returns at once, so a callback already dispatched still runs. <see cref="ITimer.DisposeAsync"/>
/// unschedules it and completes only once a callback already dispatched has returned.
/// </para>
/// <para>
/// An exception a callback throws is caught and kept in <see cref="Faults"/>, where the system timer would end the
/// process with it; a test asserts that none was thrown. <see cref="HoldNextFire"/> parks the next callback
/// dispatched before it runs, so a test can act while a timer has fired and its callback has not run yet.
/// </para>
/// <para>Test code only: nothing in production has this seam.</para>
/// </remarks>
internal sealed class ThreadedTimeProvider : TimeProvider
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly List<ThreadedTimer> _scheduled = [];
    private readonly ConcurrentQueue<Exception> _faults = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private FireHold? _hold;
    private FireHold? _readHold;
    private int _timersCreated;

    /// <summary>How many timers have been asked of this clock.</summary>
    public int TimersCreated => Volatile.Read(ref _timersCreated);

    /// <summary>Every exception a timer callback threw, in the order they were caught.</summary>
    public IReadOnlyCollection<Exception> Faults => [.. _faults];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        var readHold = Interlocked.Exchange(ref _readHold, null);
        if (readHold is not null)
        {
            readHold.Reach();
            if (!readHold.WaitForRelease(Bound))
                throw new TimeoutException("A held clock read was never released.");
        }

        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (period != Timeout.InfiniteTimeSpan)
            throw new NotSupportedException("Only one-shot timers are faked; nothing under test asks for a period.");

        Interlocked.Increment(ref _timersCreated);
        var timer = new ThreadedTimer(this, callback, state);
        Schedule(timer, dueTime);
        return timer;
    }

    /// <summary>Parks the next callback dispatched, before it runs, until the returned hold is released.</summary>
    public FireHold HoldNextFire()
    {
        var hold = new FireHold();
        Volatile.Write(ref _hold, hold);
        return hold;
    }

    /// <summary>
    /// Parks the next caller of <see cref="GetUtcNow"/>, inside the read and before it returns, until the returned
    /// hold is released: a test can act while code that reads the clock is stopped at that read.
    /// </summary>
    public FireHold HoldNextRead()
    {
        var hold = new FireHold();
        Volatile.Write(ref _readHold, hold);
        return hold;
    }

    /// <summary>
    /// Moves the clock forward and dispatches every timer that has come due to the thread pool, in due order.
    /// Returns one task per callback dispatched, which completes when that callback has returned.
    /// </summary>
    public IReadOnlyList<Task> Advance(TimeSpan delta)
    {
        List<ThreadedTimer> due;
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

        return [.. due.Select(Dispatch)];
    }

    private Task Dispatch(ThreadedTimer timer)
    {
        var hold = Interlocked.Exchange(ref _hold, null);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        timer.InFlight = done.Task;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                if (hold is not null)
                {
                    hold.Reach();
                    if (!hold.WaitForRelease(Bound))
                        throw new TimeoutException("A held timer callback was never released.");
                }

                timer.Fire();
            }
            catch (Exception ex)
            {
                _faults.Enqueue(ex);
            }
            finally
            {
                done.SetResult();
            }
        }, null);
        return done.Task;
    }

    private void Schedule(ThreadedTimer timer, TimeSpan dueTime)
    {
        lock (_gate)
        {
            _scheduled.Remove(timer);
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _now.Add(dueTime);
            if (timer.DueAt is not null)
                _scheduled.Add(timer);
        }
    }

    private void Unschedule(ThreadedTimer timer)
    {
        lock (_gate)
        {
            _scheduled.Remove(timer);
            timer.DueAt = null;
        }
    }

    /// <summary>A one-shot timer on the threaded fake clock.</summary>
    private sealed class ThreadedTimer(ThreadedTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>When the timer is due, or <see langword="null"/> once dispatched or disposed. Under the owner's lock.</summary>
        public DateTimeOffset? DueAt { get; set; }

        /// <summary>The last dispatched callback's completion; completed when none was dispatched.</summary>
        public Task InFlight { get; set; } = Task.CompletedTask;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("Only one-shot timers are faked; nothing under test asks for a period.");

            owner.Schedule(this, dueTime);
            return true;
        }

        public void Dispose() => owner.Unschedule(this);

        public async ValueTask DisposeAsync()
        {
            owner.Unschedule(this);
            await InFlight.ConfigureAwait(false);
        }

        public void Fire() => callback(state);
    }
}

/// <summary>
/// One timer callback held before it runs, or one clock read held inside it: <see cref="Reached"/> completes when it is
/// parked, and it stays parked until <see cref="Release"/>.
/// </summary>
internal sealed class FireHold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the held callback has been dispatched and is parked.</summary>
    public Task Reached => _reached.Task;

    public void Release() => _released.TrySetResult();

    internal void Reach() => _reached.TrySetResult();

    internal bool WaitForRelease(TimeSpan bound) => _released.Task.Wait(bound);
}

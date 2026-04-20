namespace Asterisk.Sdk.Resilience.Tests;

/// <summary>
/// Minimal test double for <see cref="TimeProvider"/>. Supports deterministic UtcNow,
/// <see cref="Advance(TimeSpan)"/>, and timer callbacks (including those scheduled
/// by <see cref="CancellationTokenSource"/>'s TimeProvider-aware constructor).
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now;
    private readonly List<FakeTimer> _timers = new();

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        FakeTimer[] snapshot;
        DateTimeOffset target;
        lock (_gate)
        {
            _now = _now.Add(delta);
            target = _now;
            snapshot = _timers.ToArray();
        }

        foreach (var timer in snapshot)
        {
            timer.MaybeFire(target);
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        FakeTimer[] snapshot;
        lock (_gate)
        {
            _now = value;
            snapshot = _timers.ToArray();
        }

        foreach (var timer in snapshot)
        {
            timer.MaybeFire(value);
        }
    }

    internal void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    internal sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : _provider.GetUtcNow() + dueTime;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
                return false;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : _provider.GetUtcNow() + dueTime;
            _period = period;
            return true;
        }

        public void MaybeFire(DateTimeOffset now)
        {
            if (_disposed)
                return;
            while (!_disposed && now >= _dueAt && _dueAt != DateTimeOffset.MaxValue)
            {
                _callback(_state);
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    _dueAt = DateTimeOffset.MaxValue;
                    break;
                }
                _dueAt = _dueAt + _period;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _provider.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

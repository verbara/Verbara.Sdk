namespace Asterisk.Sdk.Cluster.Primitives.Tests;

/// <summary>
/// Minimal test double for <see cref="TimeProvider"/>. Supports deterministic UtcNow
/// and <see cref="Advance(TimeSpan)"/>. Sufficient for expiry-based primitives that
/// don't need timer callbacks.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now;

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

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now = _now.Add(delta);
        }
    }
}

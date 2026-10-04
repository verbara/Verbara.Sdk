using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// Counts the events one AMI connection receives, by event name, until a marker <c>UserEvent</c> arrives; events of the
/// marker call's own channels are not counted. A test waits on a count or on the marker, never on elapsed time.
/// </summary>
internal sealed class AmiEventTally : IObserver<ManagerEvent>, IDisposable
{
    /// <summary>The channel prefix of the marker call, whose events are not part of any measurement.</summary>
    public const string MarkerChannelPrefix = "Local/marker@";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagerEvent> _events = [];
    private readonly SemaphoreSlim _changed = new(0, int.MaxValue);
    private readonly IDisposable _subscription;
    private string? _marker;
    private bool _drained;
    private bool _disposed;

    public AmiEventTally(string label, IAmiConnection connection)
    {
        Label = label;
        _subscription = connection.Subscribe(this);
    }

    /// <summary>Who this tally observes, for messages: server and user.</summary>
    public string Label { get; }

    /// <summary>Whether the marker <c>UserEvent</c> has arrived; nothing after it is counted.</summary>
    public bool Drained
    {
        get
        {
            lock (_gate)
                return _drained;
        }
    }

    /// <summary>How many events named <paramref name="eventName"/> were counted.</summary>
    public int Count(string eventName)
    {
        lock (_gate)
            return _counts.GetValueOrDefault(eventName);
    }

    /// <summary>The unique ids of the counted events named <paramref name="eventName"/>.</summary>
    public IReadOnlySet<string> UniqueIds(string eventName)
    {
        lock (_gate)
        {
            return _events.Where(e => string.Equals(e.EventType, eventName, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.UniqueId ?? Field(e, "Uniqueid") ?? "")
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The <c>Channel</c> field of the counted events named <paramref name="eventName"/>.</summary>
    public IReadOnlyList<string> Channels(string eventName)
    {
        lock (_gate)
        {
            return _events.Where(e => string.Equals(e.EventType, eventName, StringComparison.OrdinalIgnoreCase))
                .Select(e => Field(e, "Channel") ?? "")
                .ToList();
        }
    }

    /// <summary>The counts of every event name seen, for a failure message.</summary>
    public string Describe()
    {
        lock (_gate)
            return $"{Label}: " + string.Join(", ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"));
    }

    /// <summary>From now on, a <c>UserEvent</c> named <paramref name="marker"/> ends the counting.</summary>
    public void ExpectMarker(string marker)
    {
        lock (_gate)
            _marker = marker;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, re-reading it on every event; fails after <paramref name="bound"/>.</summary>
    public async Task WaitUntilAsync(Func<AmiEventTally, bool> condition, TimeSpan bound, string what)
    {
        using var timeout = new CancellationTokenSource(bound);
        while (!condition(this))
        {
            try
            {
                await _changed.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"{what} did not happen within {bound}. {Describe()}", ex);
            }
        }
    }

    public void OnNext(ManagerEvent value)
    {
        lock (_gate)
        {
            if (_drained || _disposed)
                return;

            if (_marker is not null && value is UserEventEvent && string.Equals(Field(value, "UserEvent"), _marker, StringComparison.Ordinal))
            {
                _drained = true;
            }
            else if (Field(value, "Channel")?.StartsWith(MarkerChannelPrefix, StringComparison.Ordinal) != true
                && value.EventType is { } name)
            {
                _counts[name] = _counts.GetValueOrDefault(name) + 1;
                _events.Add(value);
            }

            if (!_disposed)
                _changed.Release();
        }
    }

    public void OnError(Exception error)
    {
        // The connection reports its own failures; the waits time out and say what was counted.
    }

    public void OnCompleted()
    {
        // Completed when the connection is disposed; nothing to count.
    }

    public void Dispose()
    {
        _subscription.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _changed.Dispose();
        }
    }

    private static string? Field(ManagerEvent e, string name) =>
        e.RawFields is { } fields && fields.TryGetValue(name, out var value) ? value : null;
}

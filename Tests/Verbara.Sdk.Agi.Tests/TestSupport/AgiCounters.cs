using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Agi.Tests.TestSupport;

/// <summary>
/// Sums, per instrument name, what the SDK records on the <c>Verbara.Sdk.Agi</c> meter while this
/// capture is alive, through a <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// It matches instruments by the meter's and the instruments' names, which are the public contract an
/// exporter sees, and never by a reference to the SDK's fields: the test drives the server and reads
/// what the server emitted.
/// </para>
/// <para>
/// The instruments are process-wide and carry no tags, so this sums every connection in the process.
/// A test that asserts on it runs in <see cref="AgiMetricsGroup"/>.
/// </para>
/// </remarks>
internal sealed class AgiCounters : IDisposable
{
    /// <summary>The meter the AGI instruments are published on.</summary>
    public const string MeterName = "Verbara.Sdk.Agi";

    /// <summary>Connections the server counts as accepted.</summary>
    public const string ConnectionsAccepted = "agi.connections.accepted";

    /// <summary>Scripts the server counts as failed.</summary>
    public const string ScriptsFailed = "agi.scripts.failed";

    /// <summary>Scripts the server counts as executed.</summary>
    public const string ScriptsExecuted = "agi.scripts.executed";

    /// <summary>Requests the server counts as naming no mapped script.</summary>
    public const string ScriptsNotFound = "agi.scripts.not_found";

    /// <summary>Scripts the server counts as ended by a hangup.</summary>
    public const string Hangups = "agi.hangups";

    /// <summary>The histogram the server records once per handled connection.</summary>
    public const string ScriptDuration = "agi.script.duration";

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _sums = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _records = new(StringComparer.Ordinal);
    private readonly Lock _waitersLock = new();
    private readonly List<(string Name, long Count, TaskCompletionSource Signal)> _waiters = [];

    public AgiCounters()
        : this(MeterName)
    {
    }

    /// <summary>Sums another meter; for this helper's own test, on a meter no SDK code uses.</summary>
    internal AgiCounters(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value);
            CountRecord(instrument.Name);
        });
        _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => CountRecord(instrument.Name));
        _listener.Start();
    }

    /// <summary>The sum recorded on <c>agi.connections.accepted</c> since this capture started.</summary>
    public long Accepted => Get(ConnectionsAccepted);

    /// <summary>The sum recorded on <c>agi.scripts.failed</c> since this capture started.</summary>
    public long Failed => Get(ScriptsFailed);

    /// <summary>The sum recorded on the named instrument since this capture started.</summary>
    public long Get(string instrumentName) => _sums.GetValueOrDefault(instrumentName);

    /// <summary>
    /// How many measurements were recorded on the named instrument since this capture started,
    /// whatever their values: for a histogram such as <c>agi.script.duration</c>, the number of records.
    /// </summary>
    public long Records(string instrumentName) => _records.GetValueOrDefault(instrumentName);

    /// <summary>
    /// Completes when the named instrument has received <paramref name="count"/> records since this
    /// capture started. It is signalled from inside the record, on the thread that recorded it, so
    /// everything that thread did before the record is visible to the awaiter.
    /// </summary>
    public Task WhenRecorded(string instrumentName, long count)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            if (Records(instrumentName) >= count)
                return Task.CompletedTask;
            _waiters.Add((instrumentName, count, signal));
        }

        return signal.Task;
    }

    /// <summary>The three counters a connection test reads, for a failure message.</summary>
    public string Describe() =>
        $"accepted={Accepted} failed={Failed} executed={Get(ScriptsExecuted)}";

    public void Dispose() => _listener.Dispose();

    private void CountRecord(string instrumentName)
    {
        lock (_waitersLock)
        {
            var records = _records.AddOrUpdate(instrumentName, 1, (_, n) => n + 1);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var (name, count, signal) = _waiters[i];
                if (string.Equals(name, instrumentName, StringComparison.Ordinal) && records >= count)
                {
                    _waiters.RemoveAt(i);
                    signal.TrySetResult();
                }
            }
        }
    }
}

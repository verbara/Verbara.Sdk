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

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _sums = new(StringComparer.Ordinal);

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
            _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value));
        _listener.Start();
    }

    /// <summary>The sum recorded on <c>agi.connections.accepted</c> since this capture started.</summary>
    public long Accepted => Get(ConnectionsAccepted);

    /// <summary>The sum recorded on <c>agi.scripts.failed</c> since this capture started.</summary>
    public long Failed => Get(ScriptsFailed);

    /// <summary>The sum recorded on the named instrument since this capture started.</summary>
    public long Get(string instrumentName) => _sums.GetValueOrDefault(instrumentName);

    /// <summary>The three counters a connection test reads, for a failure message.</summary>
    public string Describe() =>
        $"accepted={Accepted} failed={Failed} executed={Get(ScriptsExecuted)}";

    public void Dispose() => _listener.Dispose();
}

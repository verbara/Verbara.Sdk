using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// Counts what the SDK records on <c>audio.transport.failures</c> of the <c>Verbara.Sdk.Ari.Audio</c>
/// meter while this counter is alive, through a <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// It matches the instrument by the meter's and the instrument's names, which are the public contract
/// an exporter sees, and never by a reference to the SDK's field: the test drives the component and
/// reads what the component emitted.
/// </para>
/// <para>
/// The instrument is process-wide and carries no tags, so this counts every session in the process.
/// A test that asserts on it runs in <see cref="AudioStreamMetricsGroup"/>.
/// </para>
/// <para>
/// The listener's callback runs on the thread that records the measurement, before
/// <c>Counter.Add</c> returns, so a read made after the component's own next step already sees it.
/// </para>
/// </remarks>
internal sealed class TransportFailureCounter : IDisposable
{
    /// <summary>The meter the ARI audio instruments are published on.</summary>
    public const string MeterName = "Verbara.Sdk.Ari.Audio";

    /// <summary>The instrument that counts transport failures under a live session.</summary>
    public const string InstrumentName = "audio.transport.failures";

    private readonly MeterListener _listener = new();
    private long _total;
    private int _measurements;

    public TransportFailureCounter()
        : this(MeterName, InstrumentName)
    {
    }

    /// <summary>
    /// Counts another instrument by its meter's and its own name: this helper's own test, on a meter no SDK
    /// code uses, and the tests of the meter's other instruments, such as <c>audio.connections.refused</c>.
    /// </summary>
    internal TransportFailureCounter(string meterName, string instrumentName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            Interlocked.Add(ref _total, value);
            Interlocked.Increment(ref _measurements);
        });
        _listener.Start();
    }

    /// <summary>The sum of the values recorded since this counter started.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>How many measurements were recorded since this counter started.</summary>
    public int Measurements => Volatile.Read(ref _measurements);

    public void Dispose() => _listener.Dispose();
}

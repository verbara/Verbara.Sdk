using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Tests.Shared.Metrics;

/// <summary>
/// Reads the samples Live records on <c>live.queue.wait_time</c> during one synchronous call made by the
/// test, and no other sample.
/// </summary>
/// <remarks>
/// <para>
/// The histogram is a process-wide static, recorded by every test that removes a queue entry, and the test
/// assemblies run their classes in parallel. <c>QueueManager.OnCallerLeft</c> records synchronously, on the
/// thread that calls it, so <see cref="During"/> keeps only a measurement whose callback runs on the calling
/// thread while the call is in progress: that thread is busy running the test's call, so nothing another test
/// records can land there.
/// </para>
/// <para>
/// It matches the instrument by the meter's and the instrument's names, the contract an exporter subscribes
/// by, so a rename fails the tests that read it.
/// </para>
/// </remarks>
internal sealed class LiveQueueWaitSamples : IDisposable
{
    public const string MeterName = "Verbara.Sdk.Live";
    public const string InstrumentName = "live.queue.wait_time";

    private const int NoThread = -1;

    private readonly MeterListener _listener = new();
    private readonly List<double> _samples = [];
    private int _recordingThread = NoThread;

    public LiveQueueWaitSamples()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == InstrumentName && instrument.Meter.Name == MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _recordingThread))
                return;
            lock (_samples)
                _samples.Add(value);
        });
        _listener.Start();
    }

    /// <summary>
    /// Runs <paramref name="call"/> and returns the samples recorded on <c>live.queue.wait_time</c> on this thread
    /// while it ran, in order.
    /// </summary>
    public IReadOnlyList<double> During(Action call)
    {
        ArgumentNullException.ThrowIfNull(call);
        int first;
        lock (_samples)
            first = _samples.Count;

        Volatile.Write(ref _recordingThread, Environment.CurrentManagedThreadId);
        try
        {
            call();
        }
        finally
        {
            Volatile.Write(ref _recordingThread, NoThread);
        }

        lock (_samples)
            return _samples.GetRange(first, _samples.Count - first);
    }

    public void Dispose() => _listener.Dispose();
}

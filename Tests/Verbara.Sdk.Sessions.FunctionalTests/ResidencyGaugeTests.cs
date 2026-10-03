using System.Diagnostics.Metrics;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that what the session manager holds is published as two gauges an operator can read: the
/// calls in progress (<c>sessions.active</c>) and the ended calls still held (<c>sessions.retained</c>),
/// separately, and that the second moves when ended calls are released.
///
/// <para>A bound that cannot be observed cannot be told apart from a bound that has stopped working —
/// the release queue wedged for the life of a process and nothing reported it. So the gauges count
/// what the manager holds, not the queue that drives its release: an ended call the queue does not
/// hold is still memory the process keeps, and counting the queue would report it gone.</para>
///
/// <para>Every manager publishes its own gauges under the <c>Verbara.Sdk.Sessions</c> meter name, and
/// this assembly runs classes in parallel, so the reader enables only the instruments of its own
/// manager's meter. The gauges are observable: each read asks the listener to collect them there and
/// then, on the test's thread, as an exporter does on its own schedule. Retention is crossed on the
/// manager's clock seam, never by waiting.</para>
/// </summary>
public sealed class ResidencyGaugeTests
{
    private const string ActiveGauge = "sessions.active";
    private const string RetainedGauge = "sessions.retained";

    /// <summary>Calls in progress the tests hold open.</summary>
    private const int Live = 2;

    /// <summary>Ended calls the tests leave held.</summary>
    private const int Ended = 3;

    [Fact]
    public async Task Gauges_ShouldPublishTheLiveAndTheEndedCallsHeldSeparately_WhenTheManagerHoldsBoth()
    {
        await using var rig = new ResidencyRig();
        OpenLiveCalls(rig);
        rig.Calls("ended", Ended);
        using var gauges = new GaugeReader(rig.Manager.ResidencyMeter);

        new { Gauges = gauges.Read(), PublicReads = PublicReads(rig) }.Should().BeEquivalentTo(
            new { Gauges = new Reading(Active: Live, Retained: Ended), PublicReads = new Reading(Live, Ended) },
            $"the {Live} calls in progress and the {Ended} ended calls still held are published separately, "
            + "and each gauge agrees with the manager's own read of the same set (ActiveSessions, "
            + $"GetRecentCompleted). Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task RetainedGauge_ShouldFallToZeroWhileTheLiveCallsStayCounted_WhenTheEndedCallsPastRetentionAreReleased()
    {
        await using var rig = new ResidencyRig();
        OpenLiveCalls(rig);
        var ended = rig.Calls("ended", Ended);
        using var gauges = new GaugeReader(rig.Manager.ResidencyMeter);
        var before = gauges.Read();

        // Past retention for every call so far, the live ones included; then one leg of a new call
        // arrives, which runs the release, and nothing ends.
        rig.MovePastRetention();
        rig.LegJoins("c-n", "n");
        ended.Should().OnlyContain(s => rig.Manager.GetById(s.SessionId) == null,
            $"premise: the arrival released every ended call past retention. Measured: {rig.Describe()}");

        new { Before = before, After = gauges.Read(), PublicReadsAfter = PublicReads(rig) }.Should().BeEquivalentTo(
            new
            {
                Before = new Reading(Active: Live, Retained: Ended),
                After = new Reading(Active: Live + 1, Retained: 0),
                PublicReadsAfter = new Reading(Live + 1, 0),
            },
            "the retained count follows the release down to what is still held, while the live calls — "
            + "older than retention too — stay counted as live beside the call that just arrived. "
            + $"Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Gauges_ShouldBePublishedUnderTheSessionsMeter_WhenAManagerIsCreated()
    {
        await using var rig = new ResidencyRig();
        using var gauges = new GaugeReader(rig.Manager.ResidencyMeter);

        gauges.Published.Should().BeEquivalentTo(
            new[]
            {
                new PublishedGauge(ActiveGauge, SessionMetrics.Meter.Name, SessionMetrics.Meter.Version, "sessions", "ObservableGauge`1"),
                new PublishedGauge(RetainedGauge, SessionMetrics.Meter.Name, SessionMetrics.Meter.Version, "sessions", "ObservableGauge`1"),
            },
            "the two gauges carry the Verbara.Sdk.Sessions meter's own name and version, so a listener or an "
            + "exporter subscribed to that meter by name — as the SDK's telemetry registration subscribes to "
            + "it — collects them beside the session counters, and no meter name is added");
    }

    [Fact]
    public async Task Gauges_ShouldBeWithdrawn_WhenTheManagerIsDisposed()
    {
        var rig = new ResidencyRig();
        rig.Calls("ended", Ended);
        using var gauges = new GaugeReader(rig.Manager.ResidencyMeter);
        gauges.Read().Should().BeEquivalentTo(new Reading(Active: 0, Retained: Ended), "premise: the gauges are published");

        await rig.DisposeAsync();

        new { Withdrawn = gauges.Withdrawn, AfterDisposal = gauges.Read() }.Should().BeEquivalentTo(
            new { Withdrawn = new[] { ActiveGauge, RetainedGauge }, AfterDisposal = new Reading(null, null) },
            "a disposed manager withdraws its gauges: their callbacks hold the manager, and a meter left "
            + "published would keep every call it held reachable for the life of the process");
    }

    private static void OpenLiveCalls(ResidencyRig rig)
    {
        for (var i = 0; i < Live; i++)
            rig.OpenAnsweredCall($"live{i}");
    }

    /// <summary>The same two sets read through the manager's public members.</summary>
    private static Reading PublicReads(ResidencyRig rig) => new(
        rig.Manager.ActiveSessions.Count(),
        rig.Manager.GetRecentCompleted(int.MaxValue).Count());

    /// <summary>One collection of the two gauges; <c>null</c> for a gauge that produced no measurement.</summary>
    private sealed record Reading(long? Active, long? Retained);

    private sealed record PublishedGauge(string Name, string MeterName, string? MeterVersion, string? Unit, string Kind);

    /// <summary>
    /// Reads one manager's gauges through a <see cref="MeterListener"/>, the way an exporter collects
    /// them, enabling only the instruments of that manager's meter. Every callback it receives runs on
    /// the thread that calls <see cref="Read"/> or disposes the manager.
    /// </summary>
    private sealed class GaugeReader : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _collected = new(StringComparer.Ordinal);
        private readonly List<PublishedGauge> _published = [];
        private readonly List<string> _withdrawn = [];

        public GaugeReader(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter, meter))
                    return;

                _published.Add(new PublishedGauge(instrument.Name, instrument.Meter.Name, instrument.Meter.Version,
                    instrument.Unit, instrument.GetType().Name));
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.MeasurementsCompleted = (instrument, _) => _withdrawn.Add(instrument.Name);
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _collected[instrument.Name] = value);
            _listener.Start();
        }

        public IReadOnlyList<PublishedGauge> Published => _published;

        public IReadOnlyList<string> Withdrawn => _withdrawn;

        public Reading Read()
        {
            _collected.Clear();
            _listener.RecordObservableInstruments();
            return new Reading(
                _collected.TryGetValue(ActiveGauge, out var active) ? active : null,
                _collected.TryGetValue(RetainedGauge, out var retained) ? retained : null);
        }

        public void Dispose() => _listener.Dispose();
    }
}

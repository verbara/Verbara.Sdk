using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A waiting caller that leaves its queue with no <c>QueueCallerLeave</c>, replayed from the transfer and bridge
/// captures (<c>Recordings/asterisk-ami/</c>, four Asterisk versions) through the server's own observer: relieved by an
/// <c>INVITE</c> with <c>Replaces</c>, relieved by an attended transfer sent as a <c>REFER</c>, and taken out of the
/// queue by <c>Action: Bridge</c>. In each the caller that joined only hangs up, and Live removes it at that
/// <c>Hangup</c> as a leave it did not observe. Plus the one conference leave of the captures that Asterisk sent after
/// its participant's <c>Hangup</c>, which Live still applies once, as observed: a <c>Hangup</c> reaches no room.
/// </summary>
/// <remarks>
/// Every input is a frame of the capture; nothing is composed. The class listens to Live's process-wide queue counters,
/// so it runs in the collection whose classes run alone (<see cref="WaitTimeHistogramGroup"/>).
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueHangupCaptureReplayTests
{
    private const string LateLeaveUniqueId = "1794455666.93";

    public static TheoryData<string> Captures => new(AmiCaptureReplay.TransferCaptures);

    [Theory]
    [MemberData(nameof(Captures))]
    public Task Hangup_ShouldRemoveTheRelievedCaller_WhenTheCapturedReplacesStepIsReplayed(string fixture) =>
        ShouldLeaveAtItsHangupAsync(fixture, "replaces-queue");

    [Theory]
    [MemberData(nameof(Captures))]
    public Task Hangup_ShouldRemoveTheRelievedCaller_WhenTheCapturedReferStepIsReplayed(string fixture) =>
        ShouldLeaveAtItsHangupAsync(fixture, "refer-queue");

    [Theory]
    [MemberData(nameof(Captures))]
    public Task Hangup_ShouldRemoveTheBridgedOutCaller_WhenTheCapturedBridgeStepIsReplayed(string fixture) =>
        ShouldLeaveAtItsHangupAsync(fixture, "bridge-queue");

    [Fact]
    public async Task ConfbridgeLeave_ShouldStillLeaveOnceAsObserved_WhenItArrivesAfterTheParticipantsHangup()
    {
        string[] steps = ["refer-conference", "refer-conference-waiting"];
        var frames = await ReadAsync(AmiCaptureReplay.LateConferenceLeaveCapture, steps);
        var hangupAt = frames.FindIndex(f => f is HangupEvent h && h.UniqueId == LateLeaveUniqueId);
        var leaveAt = frames.FindIndex(f => f is ConfbridgeLeaveEvent l && l.UniqueId == LateLeaveUniqueId);
        var left = new List<(string? UniqueId, bool LeaveNotObserved)>();

        var swallowed = await AmiCaptureReplay.ReplayStepsAsync(AmiCaptureReplay.LateConferenceLeaveCapture, steps,
            probe => probe.Server.MeetMe.UserLeft += (_, user) => left.Add((user.UniqueId, user.LeaveNotObserved)));

        using var scope = new AssertionScope();
        hangupAt.Should().BeGreaterThanOrEqualTo(0, "the capture holds the participant's Hangup");
        leaveAt.Should().BeGreaterThan(hangupAt, "in this capture the participant's leave arrived after its Hangup");
        left.Where(l => l.UniqueId == LateLeaveUniqueId).Should().Equal([(LateLeaveUniqueId, false)],
            "the participant leaves once, at the ConfbridgeLeave Asterisk sent, and Live observed it");
        swallowed.Should().BeEmpty();
    }

    private static async Task ShouldLeaveAtItsHangupAsync(string fixture, string trigger)
    {
        string[] steps = [trigger, trigger + "-waiting"];
        var frames = await ReadAsync(fixture, steps);
        var join = frames.OfType<QueueCallerJoinEvent>().Should().ContainSingle(
            "{0} {1} places one caller in the queue", fixture, trigger).Subject;
        var joined = join.UniqueId!;
        var left = new List<(string Queue, string? UniqueId, bool LeaveNotObserved)>();
        var reported = new List<string>();
        string?[] heldAtTheEnd = [];
        using var counters = new LiveQueueCounters();

        var swallowed = await AmiCaptureReplay.ReplayStepsAsync(fixture, steps,
            probe =>
            {
                probe.Server.Queues.CallerLeft += (queue, entry) => left.Add((queue, entry.UniqueId, entry.LeaveNotObserved));
                probe.Server.Queues.CallerLeaveReported += report => reported.Add(report.UniqueId);
            },
            probe => heldAtTheEnd = [.. probe.Server.Queues.Queues.SelectMany(q => q.Entries.Values).Select(e => e.UniqueId)]);

        using var scope = new AssertionScope();
        frames.OfType<QueueCallerLeaveEvent>().Where(l => l.UniqueId == joined).Should().BeEmpty(
            "Asterisk sends no QueueCallerLeave for the caller that joined");
        frames.OfType<HangupEvent>().Where(h => h.UniqueId == joined).Should().ContainSingle(
            "the caller that joined hangs up");
        heldAtTheEnd.Should().NotContain(joined, "the caller left the table at its Hangup");
        left.Where(l => l.UniqueId == joined).Should().Equal([(join.RawFields!["Queue"], joined, true)],
            "the removal is announced once, as a leave Live did not observe");
        counters.Left.Should().Be(1, "the caller that joined is the one leave counted; a channel that never joined counts none");
        counters.WaitSamples.Should().Be(0, "a leave Live did not observe records no wait sample");
        reported.Should().NotContain(joined, "no report of a leave was observed for the caller that joined");
        swallowed.Should().BeEmpty();
    }

    private static async Task<List<ManagerEvent>> ReadAsync(string fixture, string[] steps)
    {
        var frames = new List<ManagerEvent>();
        await foreach (var (_, evt) in AmiCaptureReplay.ReadStepsAsync(fixture, steps))
            frames.Add(evt);
        return frames;
    }

    /// <summary>
    /// Sums <c>live.queue.calls.left</c> and counts <c>live.queue.wait_time</c> samples while it lives, matched by the
    /// meter's and the instrument's names as an exporter subscribes.
    /// </summary>
    private sealed class LiveQueueCounters : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _left;
        private long _waitSamples;

        public LiveQueueCounters()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Live"
                    && instrument.Name is "live.queue.calls.left" or "live.queue.wait_time")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "live.queue.calls.left")
                    Interlocked.Add(ref _left, value);
            });
            _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            {
                if (instrument.Name == "live.queue.wait_time")
                    Interlocked.Increment(ref _waitSamples);
            });
            _listener.Start();
        }

        public long Left => Interlocked.Read(ref _left);

        public long WaitSamples => Interlocked.Read(ref _waitSamples);

        public void Dispose() => _listener.Dispose();
    }
}

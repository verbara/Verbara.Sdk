using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A queued caller whose call a channel verification ends — <see cref="VerbaraServer.ReconcileChannelsAsync"/> over a
/// completed snapshot that no longer lists the caller's channel, the route the sweep ends a call by — closes its queue
/// visit exactly once: <see cref="QueueSession.CallsWaiting"/> back to 0, one abandon for a visit still open, none more
/// for a visit already left with its abandon counted at app_queue's report, none for a key exit left with no event lost,
/// none more when the leave arrives after the ending, and no visit entry left in the tracker.
/// </summary>
/// <remarks>
/// <para>
/// The tracker counts each visit from the session manager's domain events and its visit signals. The verification ends
/// the call with a <see cref="CallEndedEvent"/>, the event the tracker removes its entry at; a channels-only
/// verification sends no <c>QueueStatus</c>, so no completed queue snapshot closes the visit a second way.
/// </para>
/// <para>
/// Asterisk's queue reports reach Live's queue table through its event observer, whose entry points for the leave report
/// and the abandon report are internal to <c>Verbara.Sdk.Live</c>; the test calls them through reflection, in the order
/// the observer does (the table's leave, then the report), with the event-loss epoch the server reads now.
/// </para>
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerificationClosesAQueueVisitOnceTests : IAsyncLifetime
{
    private const string Queue = "support";

    private static readonly MethodInfo ReadEventLossEpoch = typeof(VerbaraServer).GetMethod(
            "ReadEventLossEpoch", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VerbaraServer has no internal ReadEventLossEpoch.");

    private static readonly MethodInfo OnCallerLeaveReported = typeof(QueueManager).GetMethod(
            "OnCallerLeaveReported", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QueueManager has no internal OnCallerLeaveReported.");

    private static readonly MethodInfo OnCallerAbandonReported = typeof(QueueManager).GetMethod(
            "OnCallerAbandonReported", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QueueManager has no internal OnCallerAbandonReported.");

    private static readonly FieldInfo TrackerVisits = typeof(QueueSessionTracker).GetField(
            "_visits", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QueueSessionTracker has no private field _visits.");

    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    /// <summary>What the queue counted for the call, and what the tracker still keeps.</summary>
    private sealed record Closed(int CallEndedEvents, int Offered, int Waiting, int Abandoned, int TrackerEntries);

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCountAnOpenVisitAbandonedOnce_WhenTheVerificationEndsTheCall()
    {
        using var tracker = NewTracker();
        var call = _rig.QueuedCall("open", Queue);
        _rig.AsteriskLists();

        await VerifyAsync();

        ClosedOf(tracker, call).Should().BeEquivalentTo(
            new Closed(CallEndedEvents: 1, Offered: 1, Waiting: 0, Abandoned: 1, TrackerEntries: 0),
            "a visit still open when the verification ends its call left the queue unconnected: one abandon, the "
            + $"wait closed, the entry gone. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCountNoSecondAbandon_WhenTheVisitLeftWithItsAbandonReported()
    {
        using var tracker = NewTracker();
        var call = _rig.QueuedCall("abandoned", Queue);
        ReportAbandon("abandoned");
        ReportLeave("abandoned");
        var abandonedAtTheLeave = tracker.GetByQueueName(Queue)?.CallsAbandoned;
        _rig.AsteriskLists();

        await VerifyAsync();

        new { AbandonedAtTheLeave = abandonedAtTheLeave, Closed = ClosedOf(tracker, call) }.Should().BeEquivalentTo(
            new
            {
                AbandonedAtTheLeave = (int?)1,
                Closed = new Closed(CallEndedEvents: 1, Offered: 1, Waiting: 0, Abandoned: 1, TrackerEntries: 0),
            },
            "app_queue's abandon report counted the visit, its leave closed the wait, and the verification's ending "
            + $"counts nothing more. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCountNoAbandon_WhenTheVisitWasAKeyExitWithNoEventLost()
    {
        using var tracker = NewTracker();
        var call = _rig.QueuedCall("keyexit", Queue);
        ReportLeave("keyexit");
        _rig.AsteriskLists();

        await VerifyAsync();

        ClosedOf(tracker, call).Should().BeEquivalentTo(
            new Closed(CallEndedEvents: 1, Offered: 1, Waiting: 0, Abandoned: 0, TrackerEntries: 0),
            "a leave with no abandon report under an unchanged event-loss epoch is a key exit, counted neither "
            + $"answered nor abandoned, and the verification's ending does not turn it into one. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCountNothingMore_WhenTheLeaveAndTheAbandonReportArriveAfterTheEnding()
    {
        using var tracker = NewTracker();
        var call = _rig.QueuedCall("late", Queue);
        var channel = ChannelOf("late");
        _rig.AsteriskLists();

        await VerifyAsync();
        ReportAbandon("late", channel.UniqueId);
        ReportLeave("late", channel.UniqueId, channel.Name);

        ClosedOf(tracker, call).Should().BeEquivalentTo(
            new Closed(CallEndedEvents: 1, Offered: 1, Waiting: 0, Abandoned: 1, TrackerEntries: 0),
            "the ending closed the open visit once; a raw abandon report and leave arriving after it find no visit "
            + $"and count nothing. Measured: {_rig.Describe()}");
    }

    private QueueSessionTracker NewTracker() => new(_rig.Manager, Options.Create(_rig.Options));

    private Task VerifyAsync() => _rig.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);

    private Closed ClosedOf(QueueSessionTracker tracker, CallSession call)
    {
        var queue = tracker.GetByQueueName(Queue);
        return new Closed(
            _rig.EndingsOf(call),
            queue?.CallsOffered ?? -1,
            queue?.CallsWaiting ?? -1,
            queue?.CallsAbandoned ?? -1,
            ((ICollection)TrackerVisits.GetValue(tracker)!).Count);
    }

    private (string UniqueId, string Name) ChannelOf(string id)
    {
        var uniqueId = _rig.UniqueIdsOf(id).Single();
        var name = _rig.Server.Channels.GetByUniqueId(uniqueId)?.Name
            ?? throw new InvalidOperationException($"The channel table does not hold the caller of {id}.");
        return (uniqueId, name);
    }

    /// <summary>app_queue's <c>QueueCallerAbandon</c> for the caller of <paramref name="id"/>.</summary>
    private void ReportAbandon(string id, string? uniqueId = null) =>
        OnCallerAbandonReported.Invoke(_rig.Server.Queues, [uniqueId ?? ChannelOf(id).UniqueId, Queue]);

    /// <summary>
    /// app_queue's <c>QueueCallerLeave</c> for the caller of <paramref name="id"/>, as the server's event observer
    /// routes it: the table's leave, then the report, with the event-loss epoch now.
    /// </summary>
    private void ReportLeave(string id, string? uniqueId = null, string? name = null)
    {
        if (uniqueId is null || name is null)
            (uniqueId, name) = ChannelOf(id);

        _rig.Server.Queues.OnCallerLeft(Queue, name);
        var epoch = ReadEventLossEpoch.Invoke(_rig.Server, null);
        OnCallerLeaveReported.Invoke(_rig.Server.Queues, [uniqueId, Queue, name, epoch]);
    }
}

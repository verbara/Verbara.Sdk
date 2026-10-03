using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What the session manager tells the queue tracker about a caller's queue visit, from Asterisk's own reports: the
/// leave, the abandon report and a <c>QUEUESTATUS</c> of <c>TIMEOUT</c>, each naming the visit by its start, and the
/// leave saying whether an abandon was reported and whether events may have been lost since the visit opened. The
/// signals are internal, so the tests find them by name.
/// </summary>
public sealed class QueueVisitSignalTests
{
    private const string Queue = "q-sig";
    private const string Caller = "1790615800.1";
    private const string CallerChannel = "PJSIP/far-000000e1";
    private const string CallerNumber = "52800";

    [Fact]
    public async Task Leave_ShouldSignalTheVisitLeftOnceWithItsAbandonReported_WhenAsteriskReportedTheAbandonFirst()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");
        var abandoned = InternalEventProbe.Capture(rig.Manager, "VisitAbandonReported");

        rig.Deliver([.. CallerQueued(), Abandon(Queue, CallerChannel, Caller, 3), Leave(Queue, CallerChannel, Caller),
            Leave(Queue, CallerChannel, Caller)]);

        var visit = rig.Queued.Single().Timestamp;
        using var scope = new AssertionScope();
        abandoned.Should().ContainSingle();
        Signal(abandoned[0]).Should().Be(new VisitSignal(Queue, visit, AbandonReported: false, EventsMayHaveBeenLost: false));
        left.Should().ContainSingle("a visit is left once, however many leaves arrive");
        Signal(left[0]).Should().Be(new VisitSignal(Queue, visit, AbandonReported: true, EventsMayHaveBeenLost: false));
    }

    [Fact]
    public async Task Leave_ShouldSignalNoAbandonAndNoLoss_WhenTheCallerLeftByKey()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        rig.Deliver([.. CallerQueued(), Leave(Queue, CallerChannel, Caller)]);

        left.Should().ContainSingle();
        Signal(left[0]).Should().Be(new VisitSignal(Queue, rig.Queued.Single().Timestamp, AbandonReported: false,
            EventsMayHaveBeenLost: false));
    }

    [Fact]
    public async Task Leave_ShouldSignalThatEventsMayHaveBeenLost_WhenTheConnectionReconnectedSinceTheVisitOpened()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Queue", Queue)], [QueueParams(Queue, calls: 1), CallerEntry(wait: 3)]);
        rig.Deliver([Leave(Queue, CallerChannel, Caller)]);

        using var scope = new AssertionScope();
        left.Should().ContainSingle("the reload kept the visit; the leave after it closes it");
        Signal(left[0]).Should().Be(new VisitSignal(Queue, rig.Queued.Single().Timestamp, AbandonReported: false,
            EventsMayHaveBeenLost: true));
    }

    [Fact]
    public async Task QueueStatusTimeout_ShouldSignalTheVisitTimedOut_WhenTheQueueNeverConnectedIt()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var timedOut = InternalEventProbe.Capture(rig.Manager, "VisitTimedOut");

        rig.Deliver([.. CallerQueued(), Abandon(Queue, CallerChannel, Caller, 4), Leave(Queue, CallerChannel, Caller),
            VarSet(Caller, CallerChannel, "QUEUESTATUS", "LEAVEEMPTY"), VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT")]);

        timedOut.Should().ContainSingle("only TIMEOUT is a timeout");
        Signal(timedOut[0]).Should().Be(new VisitSignal(Queue, rig.Queued.Single().Timestamp, AbandonReported: false,
            EventsMayHaveBeenLost: false));
    }

    [Fact]
    public async Task QueueStatusTimeout_ShouldSignalNothing_WhenTheQueueHadConnectedTheVisit()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var timedOut = InternalEventProbe.Capture(rig.Manager, "VisitTimedOut");

        rig.Deliver([.. CallerQueued(), Leave(Queue, CallerChannel, Caller),
            AgentConnect(Queue, Caller, CallerChannel, Caller, "PJSIP/agent1", "1790615800.2", "PJSIP/agent1-000000e2", 2),
            VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT")]);

        timedOut.Should().BeEmpty("a visit app_queue connected did not time out");
    }

    [Fact]
    public async Task ReconnectReload_ShouldSignalNoLeave_WhenItClearsTheQueueTableAndFindsTheCallerStillWaiting()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Queue", Queue)], [QueueParams(Queue, calls: 1), CallerEntry(wait: 3)]);

        left.Should().BeEmpty("a reload's clear of the queue table is not a leave, and the snapshot lists the caller");
    }

    [Fact]
    public async Task Leave_ShouldSignalTheVisitLeft_WhenItArrivesAfterTheReloadClearedTheQueueTable()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        var leftBeforeTheQueueSnapshot = -1;

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)],
            new ReloadScript(
                WhileStatusIsAsked: () => rig.Deliver([Abandon(Queue, CallerChannel, Caller, 3), Leave(Queue, CallerChannel, Caller)]),
                WhileQueueStatusIsAsked: () => leftBeforeTheQueueSnapshot = left.Count));

        using var scope = new AssertionScope();
        leftBeforeTheQueueSnapshot.Should().Be(1,
            "Asterisk's leave is read when it arrives, whether or not Live's table still holds the caller");
        left.Should().ContainSingle("the visit is left once");
        InternalEventProbe.Read(left[0], "AbandonReported").Should().Be(true);
    }

    [Fact]
    public async Task CompleteQueueSnapshot_ShouldSignalTheVisitLeftWithEventsLost_WhenItNoLongerListsTheCaller()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)]);

        using var scope = new AssertionScope();
        left.Should().ContainSingle("the completed snapshot no longer lists the caller: its leave fell in the outage");
        Signal(left[0]).Should().Be(new VisitSignal(Queue, rig.Queued.Single().Timestamp, AbandonReported: false,
            EventsMayHaveBeenLost: true));
        InternalEventProbe.Read(left[0], "LeaveMissed").Should().Be(true, "the leave itself was never received");
    }

    [Fact]
    public async Task IncompleteQueueSnapshot_ShouldSignalNoLeave()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        rig.Deliver(CallerQueued());
        await rig.ReconnectAsync([CallerStatus("Wait", "30")], [QueueParams(Queue, calls: 0)], new ReloadScript(CutQueueStatusOff: true));

        left.Should().BeEmpty("a snapshot that did not complete is no evidence that the caller left");
    }

    [Fact]
    public async Task CompleteQueueSnapshot_ShouldSignalNoLeave_ForAVisitThatOpenedAfterTheSnapshotWasAskedFor()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var left = InternalEventProbe.Capture(rig.Manager, "VisitLeft");

        await rig.ReconnectAsync([], [QueueParams(Queue, calls: 0)],
            new ReloadScript(WhileQueueStatusIsAsked: () => rig.Deliver(CallerQueued())));

        left.Should().BeEmpty("the snapshot is older than the join: its silence about the caller says nothing");
    }

    [Fact]
    public async Task ReconstructedSession_ShouldSignalNothing_WhenItHasAQueueButNoVisitStart()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var signals = SignalNames.Select(name => InternalEventProbe.Capture(rig.Manager, name)).ToList();
        var session = new CallSession("reconstructed-2", Caller, QueueCallRig.ServerId, CallDirection.Inbound) { QueueName = Queue };
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = Caller, Channel = CallerChannel, Technology = "PJSIP", Role = ParticipantRole.Caller,
        });
        rig.Manager.RegisterReconstructedSession(session).Should().BeTrue("premise: the call is new to this manager");

        rig.Deliver([Abandon(Queue, CallerChannel, Caller, 3), Leave(Queue, CallerChannel, Caller),
            VarSet(Caller, CallerChannel, "QUEUESTATUS", "TIMEOUT")]);

        signals.Should().OnlyContain(s => s.Count == 0, "the manager opened no visit of this session, so it names none");
    }

    private static readonly string[] SignalNames = ["VisitLeft", "VisitAbandonReported", "VisitTimedOut"];

    // --- Reading a signal ---------------------------------------------------------------------

    private sealed record VisitSignal(string Queue, DateTimeOffset Visit, bool AbandonReported, bool EventsMayHaveBeenLost);

    private static VisitSignal Signal(object? signal) => new(
        (string)InternalEventProbe.Read(signal, "QueueName")!,
        (DateTimeOffset)InternalEventProbe.Read(signal, "Visit")!,
        (bool)InternalEventProbe.Read(signal, "AbandonReported")!,
        (bool)InternalEventProbe.Read(signal, "EventsMayHaveBeenLost")!);

    // --- The frames ---------------------------------------------------------------------------

    private static ManagerEvent[] CallerQueued() =>
    [
        NewChannel(Caller, CallerChannel, "4", Caller, CallerNumber, "from-pstn", "5011"),
        NewState(Caller, "6"),
        Join(Queue, CallerChannel, Caller, CallerNumber),
    ];

    private static StatusEvent CallerStatus(string application, string data) =>
        Status(Caller, CallerChannel, Caller, "6", CallerNumber, "from-pstn", "5011", application, data);

    private static QueueEntryEvent CallerEntry(long wait) => Entry(Queue, CallerChannel, Caller, CallerNumber, wait);
}

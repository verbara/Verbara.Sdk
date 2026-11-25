using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the connected-signal-matrix captures one call at a time and checks what each
/// <see cref="CallConnectedEvent"/> says about its own connection: its wait, its queue and its member.
/// </summary>
/// <remarks>
/// <para>
/// A connection that answers a queue visit waits from the visit's start, the instant its <see cref="CallQueuedEvent"/>
/// carries, and names that visit's queue. The call's first connection otherwise waits from the call's creation
/// (<see cref="CallSession.CreatedAt"/>), and a return from a transfer from the instant its
/// <see cref="CallTransferredEvent"/> carries; neither names a queue, a member or an agent. The call's own wait
/// (<see cref="CallSession.WaitTime"/>) stays its creation to its first connection.
/// </para>
/// <para>
/// The replay runs on the wall clock, so each wait is microseconds long; what is checked is which instant it runs
/// from, read from the notifications the same replay delivered. No kept capture holds a queued call whose transfer
/// returns by a bridge entry, nor a plain dial's transfer that returns by dial; those shapes are typed frames in
/// <see cref="ConnectedEventContentTests"/>.
/// </para>
/// </remarks>
public sealed class ConnectedEventContentReplayTests
{
    private const string Repeat = "connected-signal-matrix-repeat-asterisk-22.12.0.raw";

    public static TheoryData<string, string> EveryCall => ConnectedSignalMatrixReplayTests.EveryCall;

    public static TheoryData<string> MatrixCaptures => new(AmiCaptureReplay.ConnectedSignalMatrixCaptures);

    /// <summary>The calls a blind transfer returns to connected, and whether a queue connected them first.</summary>
    public static TheoryData<string, string, bool> TransferReturns
    {
        get
        {
            var data = new TheoryData<string, string, bool> { { Repeat, "txbridge-01", false } };
            foreach (var fixture in AmiCaptureReplay.ConnectedSignalMatrixHangupCaptures)
                data.Add(fixture, "queue-txdial-01", true);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task CallConnectedEvent_ShouldCarryTheWaitAndQueueOfItsOwnConnection_WhenACapturedCallIsReplayed(
        string fixture, string step)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("content", () => Describe(run));
        run.Session.Should().NotBeNull("premise: the replay opens the caller's call");
        var expected = Origins(run);
        expected.Should().HaveCount(run.Announcements.Count, "premise: every announcement has a connection to describe");
        for (var i = 0; i < expected.Count; i++)
        {
            var (evt, origin) = expected[i];
            evt.QueueName.Should().Be(origin.Queue,
                "announcement {0} names a queue only when its connection answers that queue's visit", i);
            evt.WaitTime.Should().Be(evt.Timestamp - origin.Start,
                "announcement {0} waits from {1}", i, origin.What);
            if (origin.Queue is null)
            {
                evt.AgentInterface.Should().BeNull("announcement {0} was made by no member's connect", i);
                evt.AgentId.Should().BeNull("announcement {0} was made by no agent's connect", i);
            }
        }

        if (run.Session!.ConnectedAt is { } connectedAt)
        {
            run.Session.WaitTime.Should().Be(connectedAt - run.Session.CreatedAt,
                "the call's own wait is still its creation to its first connection");
        }
    }

    [Theory]
    [MemberData(nameof(MatrixCaptures))]
    public async Task CallConnectedEvent_ShouldCarryTheSecondVisitsWait_WhenACapturedCallIsRequeuedAndTakenAgain(
        string fixture)
    {
        var run = await SignalMatrix.LiveAsync(fixture, "requeue-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("content", () => Describe(run));
        run.Announcements.Should().HaveCount(2, "premise: each queue connected the call once");
        var visits = run.Log.OfType<CallQueuedEvent>().ToList();
        visits.Select(v => v.QueueName).Should().Equal(["q-first", "q-second"], "premise: the call visits two queues");
        if (run.Announcements.Count != 2 || visits.Count != 2)
            return;

        var first = run.Announcements[0].Event;
        var second = run.Announcements[1].Event;
        second.WaitTime.Should().Be(second.Timestamp - visits[1].Timestamp,
            "the second announcement waits from the second visit's start, not from the call's creation");
        second.QueueName.Should().Be("q-second", "the second connection answers the second visit");
        second.AgentInterface.Should().Be("PJSIP/agent2", "the second visit's member connected it");
        first.WaitTime.Should().Be(first.Timestamp - visits[0].Timestamp, "the first announcement waits from the first visit's start");
        run.Session!.WaitTime.Should().Be(first.Timestamp - run.Session.CreatedAt,
            "the call's own wait is still its first connected time minus its creation");
    }

    [Fact]
    public async Task CallConnectedEvent_ShouldCarryEachConnectionsOwnWait_WhenACapturedCallIsAnsweredByADialThenQueued()
    {
        var run = await SignalMatrix.LiveAsync(Repeat, "aq-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("content", () => Describe(run));
        run.Announcements.Should().HaveCount(2, "premise: the dial's answer and the member's connection are each announced");
        var visit = run.Log.OfType<CallQueuedEvent>().Should().ContainSingle("premise: the answered call joins one queue").Subject;
        if (run.Announcements.Count != 2)
            return;

        var answer = run.Announcements[0].Event;
        var queued = run.Announcements[1].Event;
        answer.WaitTime.Should().Be(answer.Timestamp - run.Session!.CreatedAt,
            "the plain dial's answer is the call's first connection outside a queue: it waits from the call's creation");
        (string? QueueName, string? AgentInterface, string? AgentId) noVisit = (null, null, null);
        (answer.QueueName, answer.AgentInterface, answer.AgentId).Should().Be(noVisit,
            "the dial's answer answers no visit and no member made it");
        queued.WaitTime.Should().Be(queued.Timestamp - visit.Timestamp,
            "the queue's announcement waits from the visit's start, not from the first answer");
        (queued.QueueName, queued.AgentInterface).Should().Be(("q-second", "PJSIP/agent2"),
            "the second connection answers q-second's visit, and its member made it");
    }

    [Theory]
    [MemberData(nameof(MatrixCaptures))]
    public async Task CallConnectedEvent_ShouldCarryTheWaitSinceCreation_WhenACapturedPlainDialIsAnswered(string fixture)
    {
        var run = await SignalMatrix.LiveAsync(fixture, "plain-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        var only = run.Announcements.Should().ContainSingle("premise: the plain dial's answer is announced once").Subject.Event;
        only.WaitTime.Should().Be(only.Timestamp - run.Session!.CreatedAt,
            "a first connection outside a queue waits from the call's creation");
        run.Session.WaitTime.Should().Be(only.WaitTime, "and that is the call's own wait");
        (string? QueueName, string? AgentInterface, string? AgentId) noVisit = (null, null, null);
        (only.QueueName, only.AgentInterface, only.AgentId).Should().Be(noVisit,
            "the dial's answer answers no visit and no member made it");
    }

    [Theory]
    [MemberData(nameof(TransferReturns))]
    public async Task CallConnectedEvent_ShouldCarryTheWaitSinceTheTransferAndNameNoQueueOrMember_WhenACapturedCallReturnsFromATransfer(
        string fixture, string step, bool queuedFirst)
    {
        var run = await SignalMatrix.LiveAsync(fixture, step);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("content", () => Describe(run));
        var transfer = run.Log.OfType<CallTransferredEvent>().Should().ContainSingle("premise: the call is blind-transferred once").Subject;
        var after = run.Log.SkipWhile(e => !ReferenceEquals(e, transfer)).OfType<CallConnectedEvent>().ToList();
        var back = after.Should().ContainSingle("premise: the transfer target's answer returns the call to connected once").Subject;
        back.WaitTime.Should().Be(back.Timestamp - transfer.Timestamp,
            "the return's wait runs from the instant the call became transferring, not from the first connection's");
        (string? QueueName, string? AgentInterface, string? AgentId) noVisit = (null, null, null);
        (back.QueueName, back.AgentInterface, back.AgentId).Should().Be(noVisit,
            "the return answers no queue visit, and no member's or agent's connect made it");
        if (queuedFirst)
        {
            run.Announcements[0].Event.QueueName.Should().Be("q-first", "premise: the first connection answered q-first's visit");
            run.Session!.QueueName.Should().Be("q-first", "the call's own queue name is still the last queue it joined");
            run.Announcements.Count(a => a.Event.QueueName == "q-first").Should().Be(1,
                "the queue answered the call once, so one announcement names it; the return is not the queue's answer");
        }
    }

    // --- What each announcement should say ----------------------------------------------------

    private sealed record Origin(DateTimeOffset Start, string? Queue, string What);

    /// <summary>
    /// Each announcement with the connection it should describe, read from the notifications before it: the visit
    /// of the last <see cref="CallQueuedEvent"/>, the transfer of the last <see cref="CallTransferredEvent"/>, or,
    /// for the call's first connection when neither came before it, the call's creation.
    /// </summary>
    private static List<(CallConnectedEvent Event, Origin Origin)> Origins(SignalMatrix.Run run)
    {
        var result = new List<(CallConnectedEvent, Origin)>();
        Origin? current = run.Session is { } session ? new Origin(session.CreatedAt, null, "the call's creation") : null;
        foreach (var evt in run.Log)
        {
            switch (evt)
            {
                case CallQueuedEvent q: current = new Origin(q.Timestamp, q.QueueName, $"the {q.QueueName} visit's start"); break;
                case CallTransferredEvent t: current = new Origin(t.Timestamp, null, "the transfer"); break;
                case CallConnectedEvent c when current is not null:
                    result.Add((c, current));
                    current = null;
                    break;
            }
        }

        return result;
    }

    private static string Describe(SignalMatrix.Run run) =>
        $"created={run.Session?.CreatedAt:O} log=[{string.Join(", ", run.Log.Select(e => e switch
        {
            CallQueuedEvent q => $"queued {q.QueueName}@{q.Timestamp:O}",
            CallTransferredEvent t => $"transferred@{t.Timestamp:O}",
            CallConnectedEvent c => $"connected {c.QueueName}|{c.AgentInterface}|{c.AgentId} wait={c.WaitTime}@{c.Timestamp:O}",
            _ => e.GetType().Name,
        }))}]";
}

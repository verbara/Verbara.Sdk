using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the connected-signal-matrix captures of a caller who leaves a queue by pressing its exit key
/// (<c>Recordings/asterisk-ami/connected-signal-matrix-key-*</c>): app_queue reports no abandon, and its
/// <c>QueueCallerLeave</c> carries the exit context's <c>Context</c>, <c>Exten</c> and <c>Priority</c>, not the ones of
/// the caller's <c>QueueCallerJoin</c>. That leave ends the visit unanswered, and from the next report of the caller's
/// own channel the call is back in the state it had before the visit, as after a queue timeout.
/// </summary>
/// <remarks>
/// Each call is replayed as <see cref="SignalMatrix"/> replays it. The checkpoints are the capture's own:
/// <c>answered</c>, <c>left</c> (after the queue's leave), <c>held</c> (after the caller's hold) and <c>up</c> (after the
/// overflow dial's answer). The answered controls are the connected-signal-matrix captures of calls a queue member
/// answered, whose leave carries its join's values.
/// </remarks>
public sealed class QueueExitKeyReplayTests
{
    [Fact]
    public async Task CallConnectedEvent_ShouldBePublishedOnce_WhenAnOverflowDialAnswersACapturedCallThatLeftAQueueByItsExitKey()
    {
        var call = await SignalMatrix.ReadAsync(AmiCaptureReplay.ExitKeyCapture22, "q-key-dial-01");
        var (run, queue) = await SignalMatrix.LiveCountingAsync(call, "q-keyd");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("queue", () => queue.ToString());
        ExitKeyVisit(call, "q-keyd").Should().Be(("capture-sip|q-key-dial-in|2", "qkey-exit-dial|1|0", false),
            "premise: the leave carries the exit context's values and no abandon precedes it");
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the key exit");
        var announcement = run.Announcements.Should().ContainSingle(
            "the overflow dial's answer is the call's one connection, and it is announced once").Subject;
        announcement.Event.QueueName.Should().BeNull("the connection answers no queue visit");
        announcement.Event.AgentInterface.Should().BeNull("no queue member made the connection");
        announcement.Event.AgentId.Should().BeNull("no agent's connect made the connection");
        announcement.ConnectedAt.Should().Be(announcement.Event.Timestamp,
            "the call's connected time is its first announcement's timestamp");
        run.StateAt("up").Should().Be(CallSessionState.Connected, "the overflow dial connected the call");
        run.Session!.QueueName.Should().Be("q-keyd", "the call keeps naming the last queue it joined");
        run.Session.State.Should().Be(CallSessionState.Completed, "a connected call that hangs up normally completed");
        run.Endings.Should().ContainSingle("the call ends once");
        queue.Should().Be(new SignalMatrix.QueueCounts(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "the queue's metrics count a key exit as they did before: offered, neither answered nor abandoned");
    }

    [Fact]
    public async Task CallSession_ShouldReturnToConnectedAndCountTheHold_WhenTheCapturedAnsweredCallLeavesAQueueByItsExitKey()
    {
        var call = await SignalMatrix.ReadAsync(AmiCaptureReplay.ExitKeyCapture22, "aq-key-01");
        var run = await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        ExitKeyVisit(call, "q-key").Should().Be(("capture-sip|aq-key-in|2", "aq-exit|1|0", false),
            "premise: the leave carries the exit context's values and no abandon precedes it");
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("answered").Should().Be(CallSessionState.Connected, "premise: agent3's answer connected the call");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the key exit");
        run.StateAt("held").Should().Be(CallSessionState.OnHold,
            "the call is back in connected, silently, and the caller's hold is handled there");
        run.Held.Should().Be(1, "the hold of a call back in connected is counted");
        run.Announcements.Should().ContainSingle("only agent3's answer connected the call; the return is silent");
        run.Session!.State.Should().Be(CallSessionState.Completed, "the caller hangs up with normal clearing");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    /// <summary>
    /// The exit context is the caller's own: only <c>Exten</c> and <c>Priority</c> differ from the join. The call was
    /// answered by the dialplan before the queue and never connected, so after the key exit it ends failed, with no
    /// connected time, although its channel reported a hold.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldEndFailedWithNoConnectedTime_WhenTheCapturedDialplanAnsweredCallLeavesByAKeyInItsOwnContextAndHolds()
    {
        var call = await SignalMatrix.ReadAsync(AmiCaptureReplay.ExitKeyCapture23, "q-key-samectx-01");
        var run = await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        ExitKeyVisit(call, "q-keysame").Should().Be(("capture-sip|q-key-samectx-in|2", "capture-sip|1|0", false),
            "premise: the context is the same, the extension and priority are the exit's, and no abandon precedes it");
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the key exit");
        run.StateAt("held").Should().Be(CallSessionState.Created,
            "the caller's hold is its next report: the call is back in its state before the visit, never connected");
        run.Held.Should().Be(0, "a hold of a call nobody answered is not a held conversation");
        run.Announcements.Should().BeEmpty("nobody answered the call");
        run.Session!.ConnectedAt.Should().BeNull("nobody answered the call");
        run.Session.State.Should().Be(CallSessionState.Failed,
            "a call that went through a queue unanswered and was never connected failed, whatever its channel reported");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.FinalState.Should().Be(CallSessionState.Failed);
    }

    /// <summary>
    /// The answered controls: every visit a member answered leaves with its join's values and no abandon, so the
    /// key-exit rule leaves it alone and the member's connection is announced as before.
    /// </summary>
    [Theory]
    [InlineData("connected-signal-matrix-asterisk-22.12.0.raw", "requeue-01", 2)]
    [InlineData("connected-signal-matrix-asterisk-23.6.0.raw", "requeue-01", 2)]
    [InlineData("connected-signal-matrix-asterisk-24.0.0.raw", "requeue-01", 2)]
    [InlineData("connected-signal-matrix-repeat-asterisk-22.12.0.raw", "aq-01", 2)]
    public async Task CallConnectedEvent_ShouldBePublishedForEachAnsweredVisit_WhenACapturedAnsweredQueueCallIsReplayed(
        string fixture, string step, int connections)
    {
        var call = await SignalMatrix.ReadAsync(fixture, step);
        var run = await SignalMatrix.LiveAsync(call);

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        Visits(call).Should().NotBeEmpty("premise: the call joins a queue")
            .And.OnlyContain(v => v.Join == v.Leave && !v.Abandoned,
                "premise: every answered visit leaves with its join's values and no abandon");
        run.Swallowed.Should().Be(0, "no observer throws");
        run.Announcements.Should().HaveCount(connections, "each connection of the call is announced once");
        run.Announcements.Last().Event.QueueName.Should().Be("q-second", "q-second's member answered the last visit");
        run.Announcements.Last().Event.AgentInterface.Should().Be("PJSIP/agent2", "agent2 answered the last visit");
        run.Session!.State.Should().Be(CallSessionState.Completed, "the call ends normally");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    /// <summary>
    /// A visit whose join the SDK did not see is not judged: here a reconnect's reload finds the caller already waiting
    /// in <c>q-keyd</c> (the capture's live <c>QueueCallerJoin</c> is withheld, and the reload's <c>QueueStatus</c>
    /// lists the caller), so the visit has no join values to compare the leave with, and the call stays queued through
    /// the key exit and the overflow dial. The listed <c>QueueEntry</c> row is synthetic: no capture holds a reload
    /// taken while this caller waited.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenTheKeyExitEndsAVisitAReloadOpened()
    {
        var whole = await SignalMatrix.ReadAsync(AmiCaptureReplay.ExitKeyCapture22, "q-key-dial-01");
        var join = whole.Frames.Select((f, i) => (f, i))
            .Single(x => x.f.EventType == "QueueCallerJoin" && Raw(x.f, "Uniqueid") == whole.CallerUniqueId).i;
        var keyPress = whole.Frames.Select((f, i) => (f, i))
            .First(x => x.f.EventType == "DTMFBegin" && Raw(x.f, "Uniqueid") == whole.CallerUniqueId).i;
        var left = whole.Checkpoints.Single(c => c.Name == "left");
        var status = OfAction(whole, left.StatusId);
        var queueStatus = OfAction(whole, left.QueueStatusId);
        var entry = await QueueEntryAsync("q-keyd", Raw(whole.Frames[join], "Channel")!, whole.CallerUniqueId,
            Raw(queueStatus[0], "ActionID")!);
        List<ManagerEvent> listed = [.. queueStatus.Take(queueStatus.Count - 1), .. entry, queueStatus[^1]];
        var call = whole with { Frames = [.. whole.Frames.Where((_, i) => i != join)] };

        var run = await SignalMatrix.ReconnectingAsync(call, new SignalMatrix.Reconnect(keyPress - 1, status, listed));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        queueStatus[^1].EventType.Should().Be("QueueStatusComplete", "premise: the capture's QueueStatus ends its list");
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("reconnected-1").Should().Be(CallSessionState.Queued,
            "premise: the reload finds the caller waiting in q-keyd");
        run.StateAt("left").Should().Be(CallSessionState.Queued, "the leave of a visit the SDK did not see open is not judged");
        run.StateAt("up").Should().Be(CallSessionState.Queued,
            "with the visit not judged, the caller's dial does not return the call from it");
    }

    /// <summary>
    /// The caller's one queue visit in <paramref name="queue"/>: its join's and its leave's <c>Context|Exten|Priority</c>,
    /// and whether app_queue reported it abandoned before the leave.
    /// </summary>
    private static (string Join, string Leave, bool Abandoned) ExitKeyVisit(SignalMatrix.Call call, string queue)
    {
        var visit = Visits(call).Single(v => v.Queue == queue);
        return (visit.Join, visit.Leave, visit.Abandoned);
    }

    /// <summary>Every queue visit of the call's caller channel, in order, as the wire reports it.</summary>
    private static List<(string Queue, string Join, string Leave, bool Abandoned)> Visits(SignalMatrix.Call call)
    {
        var visits = new List<(string Queue, string Join, string Leave, bool Abandoned)>();
        string? queue = null;
        string? joinValues = null;
        var abandoned = false;
        foreach (var f in call.Frames.Where(f => Raw(f, "Uniqueid") == call.CallerUniqueId && Raw(f, "ActionID") is null))
        {
            switch (f.EventType)
            {
                case "QueueCallerJoin":
                    (queue, joinValues, abandoned) = (Raw(f, "Queue"), Location(f), false);
                    break;
                case "QueueCallerAbandon" when Raw(f, "Queue") == queue:
                    abandoned = true;
                    break;
                case "QueueCallerLeave" when Raw(f, "Queue") == queue && joinValues is not null:
                    visits.Add((queue!, joinValues, Location(f), abandoned));
                    queue = null;
                    joinValues = null;
                    break;
            }
        }

        return visits;
    }

    private static string Location(ManagerEvent f) => $"{Raw(f, "Context")}|{Raw(f, "Exten")}|{Raw(f, "Priority")}";

    private static List<ManagerEvent> OfAction(SignalMatrix.Call call, string? actionId) =>
        actionId is null
            ? throw new InvalidOperationException("premise: the checkpoint names its snapshot")
            : [.. call.Frames.Where(f => Raw(f, "ActionID") == actionId)];

    /// <summary>
    /// A synthetic <c>QueueEntry</c> row of the caller waiting in <paramref name="queue"/>, answering the capture's own
    /// <c>QueueStatus</c> action, parsed by the SDK's own reader.
    /// </summary>
    private static async Task<IReadOnlyList<ManagerEvent>> QueueEntryAsync(string queue, string channel, string uniqueId,
        string actionId)
    {
        var text = "Asterisk Call Manager/11.0.0\r\n"
            + "Event: QueueEntry\r\nQueue: " + queue + "\r\nPosition: 1\r\nChannel: " + channel + "\r\nUniqueid: " + uniqueId
            + "\r\nCallerIDNum: 82001\r\nCallerIDName: <unknown>\r\nWait: 2\r\nPriority: 0\r\nActionID: " + actionId
            + "\r\n\r\n";
        return await AmiCaptureReplay.ReadCaptureAsync(Encoding.Latin1.GetBytes(text));
    }

    private static string? Raw(ManagerEvent f, string key) => f.RawFields?.GetValueOrDefault(key);
}

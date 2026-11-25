using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the connected-signal-matrix captures of a queue visit that ends unanswered
/// (<c>Recordings/asterisk-ami/connected-signal-matrix-unanswered-*</c>): app_queue reports the visit abandoned
/// (<c>QueueCallerAbandon</c>), then the caller leaving (<c>QueueCallerLeave</c>), and the dialplan goes on with the
/// caller's channel. The call waits in <see cref="CallSessionState.Queued"/> until that channel's next report, and from
/// it the call is back in the state it had before the visit, and that report is handled there.
/// </summary>
/// <remarks>
/// Each call is replayed as <see cref="SignalMatrix"/> replays it: every live frame of the step, in capture order, to a
/// fresh server and manager. The checkpoints are the capture's own: <c>answered</c>, <c>left</c> (after the queue's
/// leave), <c>held</c> (after the caller's hold) and <c>up</c> (after the overflow dial's answer).
/// </remarks>
public sealed class UnansweredQueueVisitReplayTests
{
    [Fact]
    public async Task CallSession_ShouldReturnToTransferring_WhenAQueueATransferSentTheCapturedCallToTimesOut()
    {
        var run = await SignalMatrix.LiveAsync(AmiCaptureReplay.UnansweredVisitCapture23, "requeue-timeout-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Swallowed.Should().Be(0, "no observer throws");
        run.Transferred.Should().Be(1, "premise: agent1 blind-transfers the caller into q-tmo");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the unanswered exit");
        run.StateAt("held").Should().Be(CallSessionState.Transferring,
            "the caller's hold is its channel's next report: the call is back in the state it had before the visit, "
            + "transferred and not yet with anyone");
        run.Held.Should().Be(0, "a hold while the call is alone is not a held conversation, as it is not in queued");
        run.Announcements.Should().ContainSingle("only q-first's member answered the call")
            .Which.Event.QueueName.Should().Be("q-first");
    }

    [Fact]
    public async Task CallSession_ShouldEndCompletedOnTheWaitingCallersCause_WhenACapturedCallReturnedToTransferringHangsUp()
    {
        var run = await SignalMatrix.LiveAsync(AmiCaptureReplay.UnansweredVisitCapture23, "requeue-timeout-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.StateAt("held").Should().Be(CallSessionState.Transferring, "premise: the call is back in transferring");
        run.Session!.State.Should().Be(CallSessionState.Completed,
            "from the unanswered exit until someone answers, the call ends on the cause of the channel that waited in "
            + "the queue, which hung up with normal clearing");
        run.Session.HangupCause.Should().Be(HangupCause.NormalClearing, "the waiting channel's own cause");
        run.Endings.Should().ContainSingle("the call ends once")
            .Which.FinalState.Should().Be(CallSessionState.Completed);
    }

    [Fact]
    public async Task CallSession_ShouldReturnToConnectedAndCountTheHold_WhenAQueueTheCapturedAnsweredCallJoinedTimesOut()
    {
        var run = await SignalMatrix.LiveAsync(AmiCaptureReplay.UnansweredVisitCapture22, "aq-timeout-hold-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("answered").Should().Be(CallSessionState.Connected, "premise: agent3's answer connected the call");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the unanswered exit");
        run.StateAt("held").Should().Be(CallSessionState.OnHold,
            "the call is back in connected, silently, and the caller's hold is handled there");
        run.Held.Should().Be(1, "the hold of a call back in connected is counted");
        run.Announcements.Should().ContainSingle("only agent3's answer connected the call; the return is silent");
        run.Session!.State.Should().Be(CallSessionState.Completed, "the caller hangs up with normal clearing");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    /// <summary>
    /// A declared limit, pinned so a change to it is a decision: a call back in connected after an unanswered visit is
    /// connected already, so a later <c>Dial</c> that answers it raises no <see cref="CallConnectedEvent"/>. The same
    /// holds for two plain dials in a row with no queue between them.
    /// </summary>
    [Fact]
    public async Task CallConnectedEvent_ShouldNotBePublished_WhenAnOverflowDialAnswersACapturedCallBackInConnected()
    {
        var run = await SignalMatrix.LiveAsync(AmiCaptureReplay.UnansweredVisitCapture24, "aq-timeout-dial-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("left").Should().Be(CallSessionState.Queued, "premise: the call waits until its caller reports again");
        run.StateAt("up").Should().Be(CallSessionState.Connected,
            "the caller's dial is its next report: the call is back in connected, the state it had before the visit");
        (run.Announcements.Count - run.AnnouncementsAt("left")).Should().Be(0,
            "known limit: the call is connected already, so the overflow dial's answer is not announced");
        run.Announcements.Should().ContainSingle("only agent3's answer is announced");
        run.Session!.State.Should().Be(CallSessionState.Completed, "the call ends normally");
        run.Endings.Should().ContainSingle("the call ends once");
    }

    [Fact]
    public async Task CallConnectedEvent_ShouldBePublishedOnce_WhenAnOverflowDialAnswersACapturedCallThatTimedOutOfAQueue()
    {
        var call = await SignalMatrix.ReadAsync(AmiCaptureReplay.UnansweredVisitCapture22, "q-timeout-dial-01");
        var (run, queue) = await SignalMatrix.LiveCountingAsync(call, "q-tmo");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        scope.AddReportable("queue", () => queue.ToString());
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("left").Should().Be(CallSessionState.Queued,
            "premise: the call waits until its caller reports again after the unanswered exit");
        var announcement = run.Announcements.Should().ContainSingle(
            "the overflow dial's answer is the call's one connection, and it is announced once").Subject;
        announcement.Event.QueueName.Should().BeNull("the connection answers no queue visit");
        announcement.Event.AgentInterface.Should().BeNull("no queue member made the connection");
        announcement.Event.AgentId.Should().BeNull("no agent's connect made the connection");
        announcement.State.Should().Be(CallSessionState.Connected, "the subscriber finds the call connected");
        announcement.ConnectedAt.Should().Be(announcement.Event.Timestamp,
            "the call's connected time is its first announcement's timestamp");
        run.StateAt("up").Should().Be(CallSessionState.Connected, "the overflow dial connected the call");
        run.Session!.QueueName.Should().Be("q-tmo", "the call keeps naming the last queue it joined");
        run.Session.State.Should().Be(CallSessionState.Completed, "a connected call that hangs up normally completed");
        run.Endings.Should().ContainSingle("the call ends once");
        queue.Abandoned.Should().Be(1, "the visit q-tmo timed out of is counted abandoned");
        queue.Answered.Should().Be(0, "the overflow dial's answer is not the queue's");
        queue.Waiting.Should().Be(0, "the visit left the queue");
    }

    /// <summary>
    /// The reload arm's baseline, read from this branch: a reload taken after the leave opens the call from its
    /// channels, not in any queue, and the overflow dial's answer connects it from ringing.
    /// </summary>
    [Fact]
    public async Task CallConnectedEvent_ShouldBePublishedOnce_WhenAReloadAfterTheTimeoutOpensTheCapturedCall()
    {
        var run = await SignalMatrix.ReloadAsync(AmiCaptureReplay.UnansweredVisitCapture22, "q-timeout-dial-01");

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Swallowed.Should().Be(0, "no observer throws");
        run.StateAt("reloaded").Should().Be(CallSessionState.Ringing, "premise: the reload finds the call's channels ringing");
        run.Announcements.Should().ContainSingle("the overflow dial's answer connects the reloaded call once")
            .Which.Event.QueueName.Should().BeNull("the connection answers no queue visit");
        run.Session!.State.Should().Be(CallSessionState.Completed, "the call ends normally");
        run.Endings.Should().ContainSingle("the call ends once");
    }
}

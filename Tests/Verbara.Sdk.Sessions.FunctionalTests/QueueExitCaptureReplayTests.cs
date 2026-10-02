using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays AMI captures read with the <c>dialplan</c> class through the production parsing path into
/// <see cref="Live.Server.VerbaraServer"/>'s observer, a <see cref="Manager.CallSessionManager"/> and a
/// <see cref="Manager.QueueSessionTracker"/>, and holds each queue to what Asterisk reported in the same capture: its
/// timed-out count to the <c>QUEUESTATUS=TIMEOUT</c>s on callers that left it, its abandoned count to app_queue's
/// <c>QueueCallerAbandon</c>s for it, and its waiting count to zero after each caller's leave.
/// </summary>
public sealed class QueueExitCaptureReplayTests
{
    public static TheoryData<string> DialplanCaptures =>
        new([.. AmiCaptureReplay.QueueExitCaptures, .. AmiCaptureReplay.QueueLoopCaptures]);

    public static TheoryData<string> CallShapeCaptures => new(AmiCaptureReplay.CallShapeCaptures);

    [Theory]
    [MemberData(nameof(DialplanCaptures))]
    public async Task QueueExits_ShouldBeCountedAsAsteriskReportedThem_WhenACaptureReadWithTheDialplanClassIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueExitsAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("queues", replay.Describe());

        replay.Queues.Sum(q => q.AsteriskTimeouts).Should().BeGreaterThan(0, "premise: the capture holds a queue timeout");
        foreach (var queue in replay.Queues)
        {
            queue.TimedOut.Should().Be(queue.AsteriskTimeouts,
                "{0}: Asterisk set QUEUESTATUS=TIMEOUT {1} time(s) on callers that left it", queue.Queue, queue.AsteriskTimeouts);
            queue.Abandoned.Should().Be(queue.AsteriskAbandons,
                "{0}: app_queue reported {1} abandon(s) for it", queue.Queue, queue.AsteriskAbandons);
            queue.Waiting.Should().Be(0, "{0}: every caller has left it", queue.Queue);
        }

        replay.WaitingAfterLeaves.Should().OnlyContain(w => w.Waiting == 0,
            "after Asterisk reports that the caller left a queue, that queue counts it waiting no longer");
    }

    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task QueueTimeouts_ShouldBeCountedTwice_WhenACallShapeCaptureWithItsTwoTimeoutsIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueExitsAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("queues", replay.Describe());

        replay.Queues.Sum(q => q.AsteriskTimeouts).Should().Be(2, "premise: the two calls the empty queue gave up on");
        replay.Queues.Sum(q => q.TimedOut).Should().Be(2, "Asterisk set QUEUESTATUS=TIMEOUT on each of the two callers");
    }
}

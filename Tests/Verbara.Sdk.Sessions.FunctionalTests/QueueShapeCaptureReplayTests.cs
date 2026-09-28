using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the queue-shape AMI captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/queue-shapes-*</c>) through the production parsing path into
/// <see cref="Live.Server.VerbaraServer"/>'s observer, a <see cref="Manager.CallSessionManager"/> and a
/// <see cref="Manager.QueueSessionTracker"/>, and scores every shape against Asterisk's own verdict in
/// the same capture: app_queue's <c>AgentConnect</c> and <c>QueueCallerAbandon</c> on the caller's
/// channel.
/// </summary>
/// <remarks>
/// Neither theory asserts a terminal state or an audit trail, how many exceptions the dispatcher
/// swallowed, or a histogram sample: other changes own those, and this change must not bind them.
/// </remarks>
public sealed class QueueShapeCaptureReplayTests
{
    /// <summary>
    /// The <see cref="CallConnectedEvent"/>s the two calls that never join a queue published before
    /// queue accounting followed app_queue, as (queue, agent), identical on all three captures.
    /// Recorded on <c>bbb7bc14</c>: neither is ever announced connected. Timestamps and session ids
    /// vary from run to run, so they are not part of it.
    /// </summary>
    private static readonly Dictionary<string, (string? Queue, string? Agent)[]> NeverQueuedConnectedBaseline = new(StringComparer.Ordinal)
    {
        ["D"] = [],
        ["I"] = [],
    };

    public static TheoryData<string> QueueShapeCaptures => new(AmiCaptureReplay.QueueShapeCaptures);

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task QueueShapes_ShouldBeCountedAsAsteriskCountsThem_WhenAQueueShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("scorecard", replay.Describe());

        foreach (var shape in replay.Shapes)
        {
            shape.Answered.Should().Be(shape.AsteriskConnects,
                "{0}: app_queue connected the caller {1} time(s)", shape.Shape.Name, shape.AsteriskConnects);
            shape.Abandoned.Should().Be(shape.AsteriskAbandons,
                "{0}: app_queue reported {1} abandon(s) for the caller", shape.Shape.Name, shape.AsteriskAbandons);
            shape.WaitingLeak.Should().Be(0, "{0}: no queue counts a call as waiting once it has ended", shape.Shape.Name);
        }
    }

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task CallsThatNeverJoinAQueue_ShouldMoveNoQueueCountAndBeAnnouncedAsBefore_WhenAQueueShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("scorecard", replay.Describe());

        var neverQueued = replay.Shapes.Where(s => !s.Shape.JoinsAQueue).ToList();
        neverQueued.Select(s => s.Shape.Id).Should().Equal(["D", "I"], "the captures hold a direct dial and an IVR that never join a queue");

        foreach (var shape in neverQueued)
        {
            new QueueCounters(shape.Offered, shape.Answered, shape.Abandoned, shape.WaitingLeak).Should().Be(
                new QueueCounters(0, 0, 0, 0), "{0} never joins a queue, so no queue count moves on its events", shape.Shape.Name);
            shape.ConnectedEvents.Select(e => (e.QueueName, e.AgentId)).Should().Equal(
                NeverQueuedConnectedBaseline[shape.Shape.Id],
                "{0} is announced connected as it was before, in number, queue and agent", shape.Shape.Name);
        }
    }
}

using System.Globalization;
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

    /// <summary>
    /// The queued calls no member took although a member's leg reached the up state: <c>AX</c>, whose pooled
    /// agent must acknowledge the call and never does (the agent's leg answers and enters a bridge carrying the
    /// caller's <c>Linkedid</c>), and <c>XC</c>, whose member's phone answers and a confirmation step rejects the
    /// call. app_queue reports no connection for either, only the caller's abandon. Neither call ever connected:
    /// it ends failed, queued, with no connected time, no talk time, no <see cref="CallConnectedEvent"/>, and no
    /// <c>Connected</c> step in its trail other than the record a dial's outcome always leaves.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task QueuedCallsNoMemberTook_ShouldEndFailedWithoutEverConnecting_WhenAQueueShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("calls", DescribeCalls(replay, NobodyTook));

        foreach (var shape in NobodyTook.Select(replay.Shape))
        {
            shape.AsteriskConnects.Should().Be(0, "premise: app_queue connected {0}'s caller to no member", shape.Shape.Name);
            var call = shape.Sessions.Should().ContainSingle("{0} is one call", shape.Shape.Name).Subject;

            call.State.Should().Be(CallSessionState.Failed, "no member took {0}", shape.Shape.Name);
            call.QueuedAt.Should().NotBeNull("{0} waited in a queue", shape.Shape.Name);
            call.ConnectedAt.Should().BeNull("{0} never connected", shape.Shape.Name);
            call.TalkTime.Should().BeNull("{0} never talked", shape.Shape.Name);
            shape.ConnectedEvents.Should().BeEmpty("{0} is never announced connected", shape.Shape.Name);
            call.Events.Where(e => e.Type == CallSessionEventType.Connected
                    && e.Detail?.StartsWith("dial:", StringComparison.Ordinal) != true)
                .Should().BeEmpty("no leg reaching the up state, or entering a bridge, connected {0}", shape.Shape.Name);
        }
    }

    /// <summary>The queue shapes whose caller no member took while a member's leg reached the up state.</summary>
    private static readonly string[] NobodyTook = ["AX", "XC"];

    private static string DescribeCalls(QueueShapeReplay replay, IEnumerable<string> ids) =>
        replay.Fixture + Environment.NewLine + string.Join(Environment.NewLine, ids
            .Select(replay.Shape)
            .SelectMany(shape => shape.Sessions.Select(call => string.Create(CultureInfo.InvariantCulture,
                $"{shape.Shape.Name}: state={call.State} queuedAt={call.QueuedAt:O} connectedAt={call.ConnectedAt:O} talk={call.TalkTime} connectedEvents={shape.ConnectedEvents.Count} trail=[{string.Join(">", call.Events.Select(e => $"{e.Type}({e.Detail})"))}]"))));
}

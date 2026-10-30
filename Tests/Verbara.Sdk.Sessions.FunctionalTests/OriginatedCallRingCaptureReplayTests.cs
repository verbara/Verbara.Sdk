using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The ring of an AMI <c>Originate</c>, replayed from the twelve-shape captures of Asterisk 20.20.1, 22.9.0 and
/// 23.4.1 through the production parsing path into <see cref="Live.Server.VerbaraServer"/>'s observer
/// (<see cref="AmiCaptureReplay.ReplayAsync"/>).
/// </summary>
/// <remarks>
/// Scenario S5 originates a call to a PJSIP leg whose far end rings and never answers. The originate's
/// <c>DialBegin</c> names no calling channel, so no dial is ever attributed to the call, and the leg's
/// <c>Newstate: Ringing</c> arrives while the call is still in its initial state. That ring is recorded: the
/// call's ring time is set and its audit trail names the leg that rang. Nothing else about the call moves.
/// How many exceptions the dispatcher swallowed is not asserted here.
/// </remarks>
public sealed class OriginatedCallRingCaptureReplayTests
{
    public static TheoryData<string> CallShapeCaptures => new(AmiCaptureReplay.CallShapeCaptures);

    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task OriginatedCall_ShouldRecordItsRingAndEndAsItDoesToday_WhenItRingsAndIsNeverAnswered(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        var s5 = replay.Call("S5");
        var session = s5.Session;
        var originatedLeg = session.Participants.Select(p => p.Channel).FirstOrDefault();

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        scope.AddReportable("S5 legs", string.Join(",", session.Participants.Select(p => p.Channel)));
        scope.AddReportable("S5 audit", string.Join(">", session.Events.Select(e => $"{e.Type}({e.SourceChannel})")));

        session.Participants.Should().ContainSingle("S5 originates one leg and nothing else joins the call");
        session.RingingAt.Should().NotBeNull("the originated leg's far end rang, and the SDK observed it");
        session.Events.Where(e => e.Type == CallSessionEventType.Ringing).Should()
            .ContainSingle("the leg rang once, and that ring is recorded once")
            .Which.SourceChannel.Should().Be(originatedLeg, "the ringing entry names the originated leg");

        session.State.Should().Be(CallSessionState.Failed, "S5 is an originate the far end never answers");
        session.HangupCause.Should().Be(HangupCause.NoAnswer, "S5's originate gives up unanswered");
        s5.DomainEvents.Select(e => e.GetType().Name).Should().Equal(
            [nameof(CallStartedEvent), nameof(CallEndedEvent)],
            "recording the ring publishes nothing: S5 publishes the domain events it published before");
    }
}

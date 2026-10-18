using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;
using Xunit.Abstractions;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The committed queue captures of Asterisk 20.20.1, 22.9.0 and 23.4.1, replayed through the session manager on a
/// manual clock, each raise one <see cref="CallWrapUpEvent"/> per answered queue leg — one per app_queue
/// <c>AgentComplete</c> — none for a visit that was never answered, and none for a call whose ending only a reload
/// saw.
/// </summary>
/// <remarks>
/// The clock is moved past <see cref="SessionOptions.WrapUpDuration"/> after the last frame and before the manager
/// is disposed: the gaps between a capture's frames are seconds, so following them would close nothing on its
/// timer. A wrap-up the member's next connect closes early is raised at that connect and counts the same. No
/// committed capture carries a transfer; those are pinned on the manager directly.
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueWrapUpCaptureReplayTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Past the default wrap-up duration, so every wrap-up still open after the last frame elapses.</summary>
    private static readonly TimeSpan PastTheWrapUp = new SessionOptions().WrapUpDuration + TimeSpan.FromSeconds(1);

    public static TheoryData<string> QueueShapeCaptures => new(AmiCaptureReplay.QueueShapeCaptures);

    public static TheoryData<string> QueueReloadCaptures => new(AmiCaptureReplay.QueueReloadCaptures);

    /// <summary>
    /// Every call of every queue-reload capture, with no reload and with each of its snapshots answering a reload
    /// after an outage, as the reload pins replay them.
    /// </summary>
    public static TheoryData<string, string, string?, string?, int> ReloadReplays
    {
        get
        {
            var data = new TheoryData<string, string, string?, string?, int>();
            foreach (var fixture in AmiCaptureReplay.QueueReloadCaptures)
            {
                foreach (var caller in new[] { "5552101", "5552102", "5552103", "5552104" })
                    data.Add(fixture, caller, null, null, 0);

                data.Add(fixture, "5552101", "a1", "", 0);
                data.Add(fixture, "5552102", "b1", "QueueCallerJoin", 1);
                data.Add(fixture, "5552102", "b2", "QueueCallerLeave", 1);
                data.Add(fixture, "5552103", "c1", "QueueCallerJoin", 1);
                data.Add(fixture, "5552104", "d1", "QueueCallerLeave", 1);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task Replay_ShouldRaiseOneWrapUpPerAnsweredQueueLegAndNoneForAnUnansweredVisit_WhenTheClockPassesTheWrapUp(
        string fixture)
    {
        var clock = new ManualClock(T0);
        var timersBeforeTheClockMoved = -1;
        var replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture, clock: clock, afterLastFrame: c =>
        {
            timersBeforeTheClockMoved = c.TimersCreated;
            c.Advance(PastTheWrapUp);
        });
        var configured = new SessionOptions().WrapUpDuration;
        var elapsed = replay.Shapes.Sum(s => s.WrapUps.Count(w => w.WrapUpDuration == configured));
        var closedEarly = replay.Shapes.Sum(s => s.WrapUps.Count(w => w.WrapUpDuration < configured));

        var rows = replay.Shapes.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.Shape.Id,-4} connect={s.AsteriskConnects} complete={s.AsteriskCompletes} abandon={s.AsteriskAbandons} wrapUps={s.WrapUps.Count}")).ToList();
        output.WriteLine($"{fixture}: timers created {clock.TimersCreated}, wrap-ups elapsed {elapsed}, closed early by the member's next connect {closedEarly}, unattributed {replay.UnattributedWrapUps}");
        foreach (var row in rows)
            output.WriteLine(row);

        using var scope = new AssertionScope();
        scope.AddReportable("shapes", string.Join(Environment.NewLine, rows));
        foreach (var shape in replay.Shapes)
        {
            shape.AsteriskCompletes.Should().Be(shape.AsteriskConnects,
                "premise: app_queue ends every leg it connected in shape {0} with an AgentComplete", shape.Shape.Id);
            shape.WrapUps.Should().HaveCount(shape.AsteriskCompletes,
                "shape {0}: one wrap-up per answered queue leg, opened at its AgentComplete", shape.Shape.Id);
            if (shape.AsteriskConnects == 0)
                shape.WrapUps.Should().BeEmpty("shape {0} was never answered, so no agent wrapped up", shape.Shape.Id);
        }

        replay.Shapes.Sum(s => s.AsteriskCompletes).Should().BePositive("premise: the capture answers some visits");
        replay.Shapes.Should().Contain(s => s.Shape.JoinsAQueue && s.AsteriskConnects == 0,
            "premise: the capture holds a visit nobody answered");
        replay.UnattributedWrapUps.Should().Be(0, "every wrap-up belongs to a shape's call");
        replay.ObserverExceptions.Should().BeEmpty();
        timersBeforeTheClockMoved.Should().BePositive("wrap-ups were open on their timers when the clock moved");
        (elapsed + closedEarly).Should().Be(replay.Shapes.Sum(s => s.WrapUps.Count),
            "a wrap-up lasts the configured duration when its timer closes it, less when a connect does");
    }

    [Theory]
    [MemberData(nameof(ReloadReplays))]
    public async Task Replay_ShouldRaiseOneWrapUpPerAnsweredLeg_WhenTheCallIsReplayedWithOrWithoutAReload(
        string fixture, string caller, string? snapshot, string? outageAfter, int outageOrdinal)
    {
        var outage = outageAfter switch
        {
            null => null,
            "" => Outage.FromTheCallsFirstFrame,
            _ => Outage.After(outageAfter, outageOrdinal),
        };
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timersBeforeTheClockMoved = -1;
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, caller,
            snapshot is null ? null : CapturedSnapshot.Named(snapshot), outage, clock,
            c =>
            {
                timersBeforeTheClockMoved = c.TimersCreated;
                c.Advance(PastTheWrapUp);
            });
        output.WriteLine($"{replay.Describe()}{Environment.NewLine}   wrap-ups: {replay.WrapUps.Count}, AgentComplete delivered: {replay.DeliveredAsteriskCompletes}");

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.DeliveredAsteriskCompletes.Should().Be(1, "premise: every call of these captures is answered once and its AgentComplete delivered");
        replay.WrapUps.Should().ContainSingle("one answered queue leg is one wrap-up");
        replay.WrapUps.Should().OnlyContain(w => replay.Sessions.Any(s => s.SessionId == w.SessionId),
            "the wrap-up belongs to the replayed call");
        replay.WrapUps.Should().OnlyContain(w => w.WrapUpDuration == new SessionOptions().WrapUpDuration,
            "nothing closes the wrap-up early in a one-call replay, so it lasts the configured duration");
        timersBeforeTheClockMoved.Should().BeGreaterThan(0, "the wrap-up was open on its timer when the clock moved");
        replay.ObserverExceptions.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Replay_ShouldRaiseNoWrapUp_WhenOnlyAReloadSawTheAnsweredCallEnd(string fixture)
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, "5552101", reloadWith: null, outage: null,
            clock, c => c.Advance(PastTheWrapUp), unseenEnding: Outage.After("AgentConnect"));
        output.WriteLine($"{replay.Describe()}{Environment.NewLine}   wrap-ups: {replay.WrapUps.Count}");

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.Connected.Should().ContainSingle("premise: the SDK saw app_queue connect the caller");
        replay.DeliveredAsteriskCompletes.Should().Be(0, "premise: the call's AgentComplete fell inside the outage");
        var call = replay.Sessions.Should().ContainSingle("premise: one call").Subject;
        call.Metadata.GetValueOrDefault("cause").Should().Be("reload", "premise: the reload ended the call");
        replay.WrapUps.Should().BeEmpty(
            "a wrap-up that started at an unknown point during the outage cannot be measured, so none opens");
        replay.ObserverExceptions.Should().BeEmpty();
    }
}

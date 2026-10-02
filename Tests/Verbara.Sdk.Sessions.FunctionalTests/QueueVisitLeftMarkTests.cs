using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The session manager marks a call's current queue visit left when Asterisk reports the caller leaving that visit's
/// queue. Each test delivers Asterisk's own <c>QueueCallerLeave</c> frame to the server's event observer, so the mark
/// is read from the report it names, whichever internal path carries it.
/// </summary>
public sealed class QueueVisitLeftMarkTests
{
    private const string Caller = "1790615600.1";
    private const string CallerChannel = "PJSIP/far-000000c0";

    [Fact]
    public async Task QueueCallerLeave_ShouldMarkTheVisitLeft_WhenAsteriskReportsTheCallerLeavingItsQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("sales", CallerChannel, Caller, "100")]);
        var session = rig.Manager.GetByChannelId(Caller)!;
        session.QueueVisitLeft.Should().BeFalse("premise: a visit the manager opens starts not left");

        rig.Deliver([Leave("sales", CallerChannel, Caller)]);

        session.QueueVisitLeft.Should().BeTrue("Asterisk reported the caller leaving the queue of its visit");
    }

    [Fact]
    public async Task QueueCallerLeave_ShouldNotMarkTheVisitLeft_WhenTheLeaveIsForAnotherQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("first", CallerChannel, Caller, "100"),
            Join("second", CallerChannel, Caller, "100")]);

        rig.Deliver([Leave("first", CallerChannel, Caller)]);

        rig.Manager.GetByChannelId(Caller)!.QueueVisitLeft.Should().BeFalse(
            "the leave is for a queue the call's current visit is not in");
    }

    [Fact]
    public async Task QueueCallerLeave_ShouldNotMarkTheVisitLeft_WhenTheManagerWasDetachedFromTheServer()
    {
        await using var rig = await QueueCallRig.StartAsync();
        rig.Deliver([NewChannel(Caller, CallerChannel, "4", Caller, "100"), Join("sales", CallerChannel, Caller, "100")]);
        var session = rig.Manager.GetByChannelId(Caller)!;
        rig.Manager.DetachFromServer(QueueCallRig.ServerId);

        rig.Deliver([Leave("sales", CallerChannel, Caller)]);

        session.QueueVisitLeft.Should().BeFalse("a detached manager no longer hears the server's queue departures");
    }
}

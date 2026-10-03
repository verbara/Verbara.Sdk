using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The reconciliation sweep ends no call whose channels Asterisk still reports, whatever the call's age or
/// state: the clock only chooses when to ask.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepEndsNoLiveCallTests : IAsyncLifetime
{
    private readonly SweepRig _rig = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);

    public enum Shape
    {
        Dialing,
        Ringing,
        CreatedUnanswered,
        CreatedAnsweredByTheDialplan,
    }

    [Theory]
    [InlineData(Shape.Dialing)]
    [InlineData(Shape.Ringing)]
    [InlineData(Shape.CreatedUnanswered)]
    [InlineData(Shape.CreatedAnsweredByTheDialplan)]
    public async Task Sweep_ShouldLeaveTheCallAsItIs_WhenAsteriskStillListsItsChannels(Shape shape)
    {
        var call = shape switch
        {
            Shape.Dialing => _rig.DialingCall("c1"),
            Shape.Ringing => _rig.RingingCall("c1"),
            Shape.CreatedUnanswered => _rig.CreatedCall("c1"),
            _ => _rig.AnsweredByDialplanCall("c1"),
        };
        _rig.Age(call);
        _rig.AsteriskLists("c1");
        var before = SweepRig.Look(call);
        var sweep = _rig.BuildSweep();
        using var counters = new SessionCounters();

        await SweepRig.SweepOnceAsync(sweep);

        new
        {
            Session = SweepRig.Look(call),
            CallEndedEvents = _rig.EndingsOf(call),
            Counters = counters.Deltas,
        }.Should().BeEquivalentTo(
            new { Session = before, CallEndedEvents = 0, Counters = CounterDeltas.None },
            $"Asterisk still lists every channel of the {shape} call, so nothing proves it ended: its age only "
            + $"chooses when the sweep asks. Measured: {_rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldLetTheDialplanAnsweredCallRunToItsHangup_WhenAsteriskStillListsItsChannel()
    {
        var call = _rig.AnsweredByDialplanCall("ivr");
        _rig.Age(call);
        _rig.AsteriskLists("ivr");
        var before = SweepRig.Look(call);
        var sweep = _rig.BuildSweep();
        await SweepRig.SweepOnceAsync(sweep);
        var afterTheSweep = new { Session = SweepRig.Look(call), call.CompletedAt, CallEndedEvents = _rig.EndingsOf(call) };
        var beforeTheHangup = DateTimeOffset.UtcNow;

        _rig.HangUp("ivr", HangupCause.NormalClearing);

        new
        {
            AfterTheSweep = afterTheSweep,
            call.State,
            call.HangupCause,
            Cause = call.Metadata.GetValueOrDefault("cause"),
            CompletedAtTheHangup = call.CompletedAt >= beforeTheHangup,
            CallEndedEvents = _rig.EndingsOf(call),
        }.Should().BeEquivalentTo(
            new
            {
                AfterTheSweep = new
                {
                    Session = before,
                    CompletedAt = default(DateTimeOffset?),
                    CallEndedEvents = 0,
                },
                State = CallSessionState.Completed,
                HangupCause = (HangupCause?)HangupCause.NormalClearing,
                Cause = default(string),
                CompletedAtTheHangup = true,
                CallEndedEvents = 1,
            },
            "a call the dialplan answered and Asterisk still lists is neither ended nor marked by the sweep, so its "
            + "duration runs to the hangup that ends it, once, with that hangup's cause. "
            + $"Measured: {_rig.Describe()}");
    }
}

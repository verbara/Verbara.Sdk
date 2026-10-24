using FluentAssertions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A release against the server's handlers still in flight: the windows before a handler takes the call's lock, each
/// held open by parking the handler on the manager's step seam (<see cref="StepParker"/>), never by a sleep.
/// </summary>
/// <remarks>
/// <para>The first two tests characterize today's detach (the windows exist): green on the unfixed base, kept as pins of
/// what "keep" still does. The rest are the release's scenarios, red on the stub of the overload, which detaches and
/// releases nothing.</para>
/// <para>A handler parked past the release's limit is held past the internal 5 s drain on purpose: those tests take
/// about 6 s of real time once the release is implemented. No later event of a parked server is sent while it is
/// parked (Live's channel outbox would run it alongside the park after 1 s).</para>
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseWindowTests
{
    // --- Today's windows, characterized (green on the base; kept as pins of "keep") ------------------------------

    /// <summary>
    /// Pin, green on the base (spec: <i>Keep does not wait and does not stop a handler already running</i>): a hangup
    /// parked after its lookup and before the call's lock still ends its call after a "keep" or one-argument detach
    /// returned.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachFromServer_ShouldReturnWhileAHangupIsParkedAndTheHangupStillEndsTheCall_WhenTheDetachKeeps(
        bool namesKeep)
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.Arrival("a", "kp");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", ReleaseRig.CallerLeg("kp"));
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", ReleaseRig.CallerLeg("kp")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var detach = ReleaseRig.OnThread(() =>
        {
            if (namesKeep)
                rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
            else
                rig.Manager.DetachFromServer("a");
        });
        // Bounded below the 5 s drain: a keep that waited for the parked hangup would not return within it.
        var returnedWhileParked = ReleaseRig.Returns(detach, TimeSpan.FromSeconds(2)) && !parker.HasResumed();
        parker.LetGo();
        var hangupDone = ReleaseRig.Returns(hangup);

        new
        {
            returnedWhileParked,
            hangupDone,
            Ended = call.State is CallSessionState.Completed or CallSessionState.Failed,
            EndedNotified = rig.KindsOf(call.SessionId).Count(k => k == nameof(CallEndedEvent)),
        }.Should().BeEquivalentTo(new { returnedWhileParked = true, hangupDone = true, Ended = true, EndedNotified = 1 },
            "a keep detach waits for nothing, and the hangup it did not stop ends the call with its notification. "
            + rig.Describe());
    }

    /// <summary>
    /// Characterization, green on the base (the window before the publish): an arrival parked after it built
    /// its call and before it published it, let go after the one-argument detach returned, leaves a call held under the
    /// detached id.
    /// </summary>
    [Fact]
    public async Task OnChannelAdded_ShouldLeaveACallHeldUnderTheDetachedId_WhenAnArrivalParkedBeforeItsPublishIsLetGoAfterTheDetach()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ArrivalBuilt, "a", ReleaseRig.CallerLeg("ar"));
        var arrival = ReleaseRig.OnThread(() => rig.Arrival("a", "ar"));
        parker.WaitUntilParked(ReleaseRig.Bound);

        rig.Manager.DetachFromServer("a");
        parker.LetGo();
        var arrivalDone = ReleaseRig.Returns(arrival);

        var held = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("ar"));
        new { arrivalDone, HeldUnderA = held?.ServerId, Active = rig.Manager.ActiveSessions.Count(s => s.ServerId == "a") }
            .Should().BeEquivalentTo(new { arrivalDone = true, HeldUnderA = "a", Active = 1 },
                "the arrival completes after the detach and its call stays held under the detached id. "
                + rig.Describe());
    }

    // --- No notification of a released call SHALL follow its release notification (red on the stub) -------------

    [Fact]
    public async Task DetachFromServer_ShouldWaitForAParkedHangupAndNotReleaseItsCall_WhenItIsLetGoWithinTheLimit()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.Arrival("a", "hw");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", ReleaseRig.CallerLeg("hw"));
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", ReleaseRig.CallerLeg("hw")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returnedWhileParked = ReleaseRig.Returns(release, ReleaseRig.NotReturnedWithin);
        parker.LetGo();
        var returned = ReleaseRig.Returns(release) && ReleaseRig.Returns(hangup);

        new
        {
            returnedWhileParked,
            returned,
            Released = ReleaseRig.ResultOf(release)?.Any(c => c.SessionId == call.SessionId),
            Ended = call.State is CallSessionState.Completed or CallSessionState.Failed,
            ReleaseNotified = rig.Released.Count(e => e.SessionId == call.SessionId),
        }.Should().BeEquivalentTo(
            new { returnedWhileParked = false, returned = true, Released = (bool?)false, Ended = true, ReleaseNotified = 0 },
            "the release waits for the hangup already running; let go within the limit, the hangup ends the call, "
            + "which is then neither released nor notified as released. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldReleaseTheCallUnchangedAndLogAnError_WhenAParkedHangupOutlastsTheLimit()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.Arrival("a", "hp");
        var stateBefore = call.State;
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", ReleaseRig.CallerLeg("hp"));
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", ReleaseRig.CallerLeg("hp")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        var stateAtRelease = call.State;
        parker.LetGo();
        var hangupDone = ReleaseRig.Returns(hangup);

        new
        {
            returned,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            Errors = ErrorsNaming(rig, "a", inFlight: 1),
            StateAtRelease = stateAtRelease,
            hangupDone,
            StateAfter = call.State,
            Ended = rig.KindsOf(call.SessionId).Count(k => k == nameof(CallEndedEvent)),
            Held = rig.Manager.GetById(call.SessionId) is not null,
        }.Should().BeEquivalentTo(
            new
            {
                returned = true,
                Released = new[] { call.SessionId },
                Errors = 1,
                StateAtRelease = stateBefore,
                hangupDone = true,
                StateAfter = stateBefore,
                Ended = 0,
                Held = false,
            },
            "past the limit the release logs one Error naming a and one handler in flight and releases the call "
            + "unchanged; the hangup let go afterwards ends nothing and notifies nothing. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldReleaseACallOpenedByAParkedArrival_WhenItIsLetGoWithinTheLimit()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ArrivalBuilt, "a", ReleaseRig.CallerLeg("l2"));
        var arrival = ReleaseRig.OnThread(() => rig.Arrival("a", "l2"));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returnedWhileParked = ReleaseRig.Returns(release, ReleaseRig.NotReturnedWithin);
        parker.LetGo();
        var returned = ReleaseRig.Returns(release) && ReleaseRig.Returns(arrival);

        var opened = ReleaseRig.ResultOf(release)?.SingleOrDefault(c => c.LinkedId == ReleaseRig.LinkedIdOf("l2"));
        new
        {
            returnedWhileParked,
            returned,
            Released = opened is not null,
            ByLinkedId = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("l2")) is null,
            LastNotification = opened is null ? null : rig.KindsOf(opened.SessionId).LastOrDefault(),
        }.Should().BeEquivalentTo(
            new
            {
                returnedWhileParked = false, returned = true, Released = true, ByLinkedId = true,
                LastNotification = nameof(CallReleasedEvent),
            },
            "the release waits for the arrival already running; the call it opened within the limit is released "
            + "with the others, its linkedid forgotten, its release the last notification. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldLeaveNoEntryOnTheReleasedCall_WhenALegOfAnotherServerJoiningItIsLetGoAfterTheRelease()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        var call = rig.ConnectedCall("a", "xj");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.JoinFound, "b", "b-leg-xj");
        var join = ReleaseRig.OnThread(() => rig["b"].Server.Channels.OnNewChannel("b-leg-xj", "PJSIP/300-xj",
            Enums.ChannelState.Up, context: "from-trunk", linkedId: ReleaseRig.LinkedIdOf("xj")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        parker.LetGo();
        var joined = ReleaseRig.Returns(join);

        var legsCall = rig.Manager.GetByChannelId("b-leg-xj");
        var kinds = rig.KindsOf(call.SessionId);
        new
        {
            returned,
            joined,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            NamesC = ReferenceEquals(rig.Manager.GetById(call.SessionId), call)
                || ReferenceEquals(rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("xj")), call)
                || ReferenceEquals(rig.Manager.GetByChannelId(ReleaseRig.CallerLeg("xj")), call)
                || ReferenceEquals(rig.Manager.GetByChannelId(ReleaseRig.CalleeLeg("xj")), call)
                || ReferenceEquals(legsCall, call),
            LegsCallOfB = legsCall is not null && !ReferenceEquals(legsCall, call) && legsCall.ServerId == "b",
            AfterRelease = kinds.SkipWhile(k => k != nameof(CallReleasedEvent)).Skip(1).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                returned = true, joined = true, Released = new[] { call.SessionId }, NamesC = false,
                LegsCallOfB = true, AfterRelease = Array.Empty<string>(),
            },
            "a leg of b that found the call by its linkedid before the release and is let go after it writes no "
            + "entry naming the released call and opens a call of its own on b; nothing follows the release "
            + "notification. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldNotOverlapAnAttachOfTheSameId_WhenTheReleaseGoesFirst()
    {
        await using var rig = await ReleaseRig.StartAsync("n1");
        var old = new[] { rig.ConnectedCall("n1", "o1"), rig.ConnectedCall("n1", "o2") };
        var fresh = await rig.NewInstanceAsync("n1");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ReleaseWalkStarted, "n1");
        var release = ReleaseRig.OnThread(() => rig.Release("n1"));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var attach = ReleaseRig.OnThread(() => rig.Manager.AttachToServer(fresh.Server, "n1"));
        var attachReturnedWhileReleasing = ReleaseRig.Returns(attach, ReleaseRig.NotReturnedWithin);
        parker.LetGo();
        var bothReturned = ReleaseRig.Returns(release) && ReleaseRig.Returns(attach);
        rig.Current(fresh);
        var newCall = rig.Arrival("n1", "f1");

        new
        {
            attachReturnedWhileReleasing,
            bothReturned,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            NewCallHeld = ReferenceEquals(rig.Manager.GetById(newCall.SessionId), newCall) && newCall.ServerId == "n1",
        }.Should().BeEquivalentTo(
            new
            {
                attachReturnedWhileReleasing = false,
                bothReturned = true,
                Released = old.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                NewCallHeld = true,
            },
            "an attach of n1 that finds n1's release in progress waits for it; the release returns exactly the 2 calls "
            + "of the earlier instance and the new attachment's subscriptions are intact. " + rig.Describe());
    }

    /// <summary>
    /// The Error entries naming <paramref name="serverId"/> as a value and carrying <paramref name="inFlight"/> as a
    /// numeric value (the handlers still in flight), optionally also naming <paramref name="skipped"/>.
    /// </summary>
    internal static int ErrorsNaming(ReleaseRig rig, string serverId, int inFlight, string? skipped = null) =>
        rig.Log.At(LogLevel.Error).Count(e =>
            e.Values.Values.Contains(serverId)
            && e.Values.Values.Any(v => v is int or long && Convert.ToInt64(v, null) == inFlight)
            && (skipped is null || e.Message.Contains(skipped, StringComparison.Ordinal)));
}

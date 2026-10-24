using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A release against what a call leaves behind: an arrival stopped between its two index writes (a), the agents' and
/// members' connected-leg counts a released call must give back (b), a member's completion still in flight when its
/// call is released (c), and the scope of the release's bound — a call-released subscriber that blocks holds the
/// release and an attach waiting for it (d).
/// </summary>
/// <remarks>
/// Red on the stub of the overload, which detaches and releases nothing, save that the last test pins the bound's
/// stated scope and has no revert-red. The wrap-up's duration runs on <see cref="FakeTimeProvider"/>; the release's
/// own limits are wall-clock, so the tests that hold a handler past them take about 6 s of real time once the release is
/// implemented. No sleep anywhere.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseLegsAndBoundTests
{
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private const string Member = "PJSIP/agent1";

    // --- (a) An arrival stopped between its two index writes past the bound ---------------------------------

    [Fact]
    public async Task DetachFromServer_ShouldSkipThenReleaseAnArrival_WhenItIsStoppedBetweenItsTwoIndexWritesPastTheBound()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var leg = ReleaseRig.CallerLeg("hb");
        var linkedId = ReleaseRig.LinkedIdOf("hb");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ArrivalHeldById, "a", leg);
        var arrival = ReleaseRig.OnThread(() => rig.Arrival("a", "hb"));
        parker.WaitUntilParked(ReleaseRig.Bound);
        var session = rig.Manager.ActiveSessions.Single(s => s.LinkedId == linkedId);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        var first = new
        {
            returned,
            ContainsS = ReleaseRig.ResultOf(release)?.Any(c => c.SessionId == session.SessionId),
            Errors = CallSessionManagerDetachReleaseWindowTests.ErrorsNaming(rig, "a", inFlight: 1, skipped: session.SessionId),
        };
        parker.LetGo();
        var arrivalDone = ReleaseRig.Returns(arrival);
        var afterLetGo = new
        {
            arrivalDone,
            ById = ReferenceEquals(rig.Manager.GetById(session.SessionId), session),
            ByLinkedId = ReferenceEquals(rig.Manager.GetByLinkedId(linkedId), session),
            ByChannel = ReferenceEquals(rig.Manager.GetByChannelId(leg), session),
            Started = rig.KindsOf(session.SessionId).Count(k => k == nameof(CallStartedEvent)),
        };

        var again = rig.Release("a");
        var kinds = rig.KindsOf(session.SessionId);
        var reconstruction = new CallSession(Guid.NewGuid().ToString("N"), linkedId, "a", CallDirection.Inbound);

        new
        {
            First = first,
            AfterLetGo = afterLetGo,
            Again = again.Select(c => c.SessionId).ToArray(),
            ReleasedAfterStarted = Array.IndexOf(kinds, nameof(CallReleasedEvent)) > Array.IndexOf(kinds, nameof(CallStartedEvent))
                && Array.IndexOf(kinds, nameof(CallStartedEvent)) >= 0,
            AllForgotten = rig.Manager.GetById(session.SessionId) is null && rig.Manager.GetByLinkedId(linkedId) is null
                && rig.Manager.GetByChannelId(leg) is null,
            ReconstructionAccepted = rig.Manager.RegisterReconstructedSession(reconstruction),
        }.Should().BeEquivalentTo(
            new
            {
                First = new { returned = true, ContainsS = (bool?)false, Errors = 1 },
                AfterLetGo = new { arrivalDone = true, ById = true, ByLinkedId = true, ByChannel = true, Started = 1 },
                Again = new[] { session.SessionId },
                ReleasedAfterStarted = true,
                AllForgotten = true,
                ReconstructionAccepted = true,
            },
            "a release past the limit skips the arrival stopped between its id and linkedid writes (one Error naming a, "
            + "one handler in flight and S); once let go S is fully published and announced once; a later release "
            + "takes it after its start, forgets it everywhere, and a reconstruction under its linkedid is accepted. "
            + rig.Describe());
    }

    // --- (b) The released call's legs leave the wrap-up's connected counts -----------------------------------

    [Fact]
    public async Task DetachFromServer_ShouldTakeTheReleasedCallsLegOffTheWrapUpCount_WhenTheAgentTakesAnotherCall()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a", "b"]);
        using var agents = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        rig["a"].Deliver(AgentLogin("agent-1", Member, "login-a"));
        rig["b"].Deliver(AgentLogin("agent-1", Member, "login-b"));
        rig["a"].Deliver(NamedAgentCallConnected("x1", "x1m", Member, "agent-1"));
        var released = rig.Manager.GetByLinkedId("x1")!;

        var returned = rig.Release("a");
        var wrapUpsAtRelease = WrapUpsOf(rig).Length;
        rig["b"].Deliver(NamedAgentCallConnected("y1", "y1m", Member, "agent-1"));
        var next = rig.Manager.GetByLinkedId("y1")!;
        rig["b"].Deliver(NamedAgentCallEnds("y1", "y1m"));
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            Returned = returned.Select(c => c.SessionId).ToArray(),
            WrapUpsAtRelease = wrapUpsAtRelease,
            WrapUps = WrapUpsOf(rig).Select(w => w.SessionId).ToArray(),
            agents.GetByAgentId("agent-1")!.State,
        }.Should().BeEquivalentTo(
            new
            {
                Returned = new[] { released.SessionId },
                WrapUpsAtRelease = 0,
                WrapUps = new[] { next.SessionId },
                State = AgentSessionState.Idle,
            },
            "the released call's leg leaves the agent's connected count without a wrap-up, so the agent's next call "
            + "on b opens one when it ends and the tracker reports the agent idle after it. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldTakeAMembersLegOffItsWrapUpCount_WhenTheSameServerIdReturns()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        rig["a"].Deliver(QueueCallConnected("q1", "c1", "m1", Member));
        var released = rig.Manager.GetByLinkedId("c1")!;

        var returned = rig.Release("a");
        var returning = await rig.AttachAsync("a");
        returning.Deliver(QueueCallConnected("q1", "c2", "m2", Member));
        var next = rig.Manager.GetByLinkedId("c2")!;
        returning.Deliver(MemberHangsUp("q1", "c2", "m2", Member));
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            Returned = returned.Select(c => c.SessionId).ToArray(),
            WrapUps = WrapUpsOf(rig).Select(w => w.SessionId).ToArray(),
        }.Should().BeEquivalentTo(
            new { Returned = new[] { released.SessionId }, WrapUps = new[] { next.SessionId } },
            "the released call's member leg leaves the member's count, so when the same server id returns the member's "
            + "completion of its next queue call opens one wrap-up. " + rig.Describe());
    }

    // --- (c) A member's completion in flight when its call is released --------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnQueueCallerCompleted_ShouldOpenNoWrapUp_WhenItsCallIsReleasedWhileTheHandlerIsInFlight(bool withProvider)
    {
        var clock = new FakeTimeProvider();
        using var asked = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        var provider = withProvider ? new GatedProvider(asked, answer, WrapUp) : null;
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            wrapUpDurations: provider, serverIds: ["a"]);
        var a = rig["a"];
        a.Deliver(QueueCallConnected("q1", "cc", "ccm", Member));
        var call = rig.Manager.GetByLinkedId("cc")!;
        a.Deliver(BridgeLeave(Bridge("cc"), "ccm"), Hangup("ccm"));

        using var parker = withProvider ? null : StepParker.Install(rig.Manager, HandlerStep.AgentCompleteFound, "a", "cc");
        var complete = ReleaseRig.OnThread(() => a.Deliver(
            AgentComplete("q1", "cc", CallerChannel("cc"), "cc", Member, "ccm", MemberChannel(Member, "ccm"), 30)));
        try
        {
            if (parker is not null)
                parker.WaitUntilParked(ReleaseRig.Bound);
            else
                asked.Wait(ReleaseRig.Bound).Should().BeTrue("premise: the provider is being asked");

            var release = ReleaseRig.OnThread(() => rig.Release("a"));
            var returned = ReleaseRig.Returns(release);
            parker?.LetGo();
            answer.Set();
            var completed = ReleaseRig.Returns(complete);
            clock.Advance(WrapUp);
            clock.Advance(TimeSpan.FromMinutes(5));

            new
            {
                returned,
                Released = ReleaseRig.ResultOf(release)?.Any(c => c.SessionId == call.SessionId),
                completed,
                WrapUps = WrapUpsOf(rig).Count(w => w.SessionId == call.SessionId),
            }.Should().BeEquivalentTo(
                new { returned = true, Released = (bool?)true, completed = true, WrapUps = 0 },
                $"a member's completion {(withProvider ? "asking the provider" : "parked before the call's lock")} "
                + "when its call is released past the limit opens no wrap-up for the released call. " + rig.Describe());
        }
        finally
        {
            parker?.LetGo();
            answer.Set();
        }
    }

    // --- (d) The bound does not cover a call-released subscriber (a pin, no revert-red) ---------------------

    [Fact]
    public async Task DetachFromServer_ShouldWaitForACallReleasedSubscriber_WhenItBlocks()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var c1 = rig.ConnectedCall("a", "w1");
        var c2 = rig.ConnectedCall("a", "w2");
        var fresh = await rig.NewInstanceAsync("a");
        using var blocked = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == c1.SessionId)
            {
                blocked.Set();
                gate.Wait(ReleaseRig.Bound);
            }
        }));

        try
        {
            var release = ReleaseRig.OnThread(() => rig.Release("a"));
            var blockedSeen = WaitHandle.WaitAny([blocked.WaitHandle, ((IAsyncResult)release).AsyncWaitHandle],
                ReleaseRig.Bound) >= 0 && blocked.IsSet;
            long attachReturnedAt = -1;
            var attach = ReleaseRig.OnThread(() =>
            {
                rig.Manager.AttachToServer(fresh.Server, "a");
                attachReturnedAt = rig.Sequence;
            });
            var attachReturnedWhileBlocked = ReleaseRig.Returns(attach, ReleaseRig.NotReturnedWithin);
            var releaseReturnedWhileBlocked = release.IsCompleted;
            var errorsWhileBlocked = rig.Log.At(Microsoft.Extensions.Logging.LogLevel.Error).Count;
            gate.Set();
            var bothReturned = ReleaseRig.Returns(release) && ReleaseRig.Returns(attach);
            rig.Current(fresh);
            var newCall = rig.Arrival("a", "w3");
            var releasedAt = rig.Events.Where(e => e.Event is CallReleasedEvent).Select(e => e.Sequence).ToArray();

            new
            {
                blockedSeen,
                attachReturnedWhileBlocked,
                releaseReturnedWhileBlocked,
                errorsWhileBlocked,
                bothReturned,
                Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
                NotifiedBeforeAttachReturned = releasedAt.Length == 2 && releasedAt.All(s => s <= attachReturnedAt),
                NewAttachmentIntact = ReferenceEquals(rig.Manager.GetById(newCall.SessionId), newCall)
                    && newCall.ServerId == "a",
            }.Should().BeEquivalentTo(
                new
                {
                    blockedSeen = true,
                    attachReturnedWhileBlocked = false,
                    releaseReturnedWhileBlocked = false,
                    errorsWhileBlocked = 0,
                    bothReturned = true,
                    Released = new[] { c1.SessionId, c2.SessionId },
                    NotifiedBeforeAttachReturned = true,
                    NewAttachmentIntact = true,
                },
                options => options.WithoutStrictOrderingFor(x => x.Released),
                "a call-released subscriber that blocks holds the release and an attach of the same id waiting for it, "
                + "with no Error entry; let go, the release returns both calls, every release notification precedes the "
                + "attach's return, and the new attachment is intact. " + rig.Describe());
        }
        finally
        {
            gate.Set();
        }
    }

    private static CallWrapUpEvent[] WrapUpsOf(ReleaseRig rig) =>
        [.. rig.Events.Select(e => e.Event).OfType<CallWrapUpEvent>()];

    /// <summary>A wrap-up duration provider that signals it is being asked and answers only once let go.</summary>
    private sealed class GatedProvider(ManualResetEventSlim asked, ManualResetEventSlim answer, TimeSpan duration)
        : IWrapUpDurationProvider
    {
        public TimeSpan? GetWrapUpDuration(string serverId, string queueName)
        {
            asked.Set();
            answer.Wait(ReleaseRig.Bound);
            return duration;
        }
    }
}

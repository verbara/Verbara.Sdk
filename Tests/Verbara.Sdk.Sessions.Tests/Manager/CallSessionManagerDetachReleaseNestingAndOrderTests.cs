using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Four properties of a release: a release or an attach requested from inside the release's own notification is
/// refused, and a second release waits for the first (a); the release notification is the last notification of its
/// call, which needs every notification raised under the call's lock (b); the release's own waits are bounded and a
/// subscriber's failure stops nothing (c); and a refusal a subscriber handles or lets escape stops neither the release
/// nor the delivery (d).
/// </summary>
/// <remarks>
/// Red on the stub of the overload, which detaches and releases nothing. Every wait is bounded; a handler or a
/// subscriber held open is held on the step seam or on a gate the test opens, never by a sleep. Runs in
/// <see cref="SessionSubscriberFaultsGroup"/>: it counts <c>sessions.events.subscriber_faults</c>.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseNestingAndOrderTests
{
    private static readonly int[] OneEach = [1, 1];

    // --- (a) Refusals from inside the release's notification, and a second release ----------------------

    [Theory]
    [InlineData("srv-a")]
    [InlineData("srv-b")]
    public async Task DetachFromServer_ShouldRefuseANestedRelease_WhenRequestedFromInsideCallReleasedEvent(string nested)
    {
        await using var rig = await ReleaseRig.StartAsync("srv-a", "srv-b");
        var c1 = rig.ConnectedCall("srv-a", "p1");
        var c2 = rig.ConnectedCall("srv-a", "p2");
        var ofB = rig.ConnectedCall("srv-b", "pb");
        Exception? inner = null;
        IReadOnlyList<CallSession>? innerResult = null;
        var asked = 0;
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == c1.SessionId && Interlocked.Exchange(ref asked, 1) == 0)
            {
                try
                {
                    innerResult = rig.Manager.DetachFromServer(nested, DetachedCalls.Release);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    inner = ex;
                }
            }
        }));

        var outer = ReleaseRig.OnThread(() => rig.Release("srv-a"));
        var returned = ReleaseRig.Returns(outer);
        rig["srv-b"].Server.Channels.OnHold(ReleaseRig.CallerLeg("pb"), "default");

        new
        {
            returned,
            Inner = inner?.GetType().Name,
            InnerNamesTheId = inner?.Message.Contains(nested, StringComparison.Ordinal) == true,
            InnerReturned = innerResult is not null,
            PerCall = new[] { c1, c2 }.Select(c => rig.Released.Count(r => r.SessionId == c.SessionId)).ToArray(),
            Outer = ReleaseRig.ResultOf(outer)?.Select(c => c.SessionId).ToArray(),
            Forgotten = rig.Manager.GetById(c1.SessionId) is null && rig.Manager.GetById(c2.SessionId) is null,
            BHeldAndAttached = nested != "srv-b"
                || (ReferenceEquals(rig.Manager.GetById(ofB.SessionId), ofB) && ofB.State == CallSessionState.OnHold),
        }.Should().BeEquivalentTo(
            new
            {
                returned = true,
                Inner = nameof(InvalidOperationException),
                InnerNamesTheId = true,
                InnerReturned = false,
                PerCall = OneEach,
                Outer = new[] { c1.SessionId, c2.SessionId },
                Forgotten = true,
                BHeldAndAttached = true,
            },
            options => options.WithoutStrictOrderingFor(x => x.Outer),
            $"a release of {nested} requested from inside C1's call-released notification is refused with "
            + "InvalidOperationException naming the id; each call of srv-a is notified once, the outer release returns "
            + "both and forgets them, and srv-b stays attached with its call held. " + rig.Describe());
    }

    [Fact]
    public async Task AttachToServer_ShouldRefuse_WhenRequestedFromInsideCallReleasedEventOfTheIdBeingReleased()
    {
        await using var rig = await ReleaseRig.StartAsync("srv-a");
        var c1 = rig.ConnectedCall("srv-a", "q1");
        var fresh = await rig.NewInstanceAsync("srv-a");
        Exception? attachFault = null;
        var attached = false;
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == c1.SessionId)
            {
                try
                {
                    rig.Manager.AttachToServer(fresh.Server, "srv-a");
                    attached = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    attachFault = ex;
                }
            }
        }));

        var release = ReleaseRig.OnThread(() => rig.Release("srv-a"));
        var returned = ReleaseRig.Returns(release);

        new
        {
            returned,
            Fault = attachFault?.GetType().Name,
            NamesA = attachFault?.Message.Contains("srv-a", StringComparison.Ordinal) == true,
            attached,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                returned = true, Fault = nameof(InvalidOperationException), NamesA = true, attached = false,
                Released = new[] { c1.SessionId },
            },
            "an attach of a requested from inside the call-released notification of a's release in progress would "
            + "wait for its own thread: it is refused with InvalidOperationException naming a, and the release returns "
            + "C1. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldWaitForTheReleaseInProgress_WhenASecondReleaseOfTheIdIsRequested()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var calls = new[] { rig.ConnectedCall("a", "s1"), rig.ConnectedCall("a", "s2") };
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ReleaseWalkStarted, "a");
        var first = ReleaseRig.OnThread(() => rig.Release("a"));
        parker.WaitUntilParked(ReleaseRig.Bound);

        long secondReturnedAt = -1;
        var second = ReleaseRig.OnThread(() =>
        {
            var result = rig.Release("a");
            secondReturnedAt = rig.Sequence;
            return result;
        });
        var secondReturnedWhileParked = ReleaseRig.Returns(second, ReleaseRig.NotReturnedWithin);
        parker.LetGo();
        var bothReturned = ReleaseRig.Returns(first) && ReleaseRig.Returns(second);

        var releasedAt = rig.Events.Where(e => e.Event is CallReleasedEvent).Select(e => e.Sequence).ToArray();
        new
        {
            secondReturnedWhileParked,
            bothReturned,
            First = ReleaseRig.ResultOf(first)?.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Second = ReleaseRig.ResultOf(second)?.Count,
            PerCall = calls.Select(c => rig.Released.Count(r => r.SessionId == c.SessionId)).ToArray(),
            AllBeforeSecondReturned = releasedAt.Length == 2 && releasedAt.All(s => s <= secondReturnedAt),
        }.Should().BeEquivalentTo(
            new
            {
                secondReturnedWhileParked = false,
                bothReturned = true,
                First = calls.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                Second = (int?)0,
                PerCall = OneEach,
                AllBeforeSecondReturned = true,
            },
            "a second release of a while the first is parked after its drain has not returned; once the first is let "
            + "go it returns both calls, the second none, one notification per call, every one raised before the "
            + "second returned. " + rig.Describe());
    }

    // --- (b) The release notification is the last; every notification under the call's lock ------------

    [Fact]
    public async Task CallReleasedEvent_ShouldBeTheLastNotification_WhenAnotherServersQueueJoinIsInFlight()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        using var queues = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));
        var call = rig.Arrival("a", "qj");
        var caller = ReleaseRig.CallerLeg("qj");
        using var joinParker = StepParker.Install(rig.Manager, HandlerStep.QueueVisitWritten, "b", caller);
        using var walkParker = new StepParker(HandlerStep.ReleaseWalkStarted, "a") { WhenReached = joinParker.LetGo };
        walkParker.Install(rig.Manager);

        var join = ReleaseRig.OnThread(() => rig["b"].Deliver(Join("q1", "PJSIP/trunk-qj-a", caller, ReleaseRig.LinkedIdOf("qj"))));
        joinParker.WaitUntilParked(ReleaseRig.Bound);
        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        walkParker.WaitUntilParked(ReleaseRig.Bound);
        walkParker.LetGo();
        var returned = ReleaseRig.Returns(release) && ReleaseRig.Returns(join);

        var kinds = rig.KindsOf(call.SessionId);
        new
        {
            returned,
            Last = kinds.LastOrDefault(),
            QueuedAfterRelease = kinds.SkipWhile(k => k != nameof(CallReleasedEvent)).Contains(nameof(CallQueuedEvent)),
            Waiting = queues.GetByQueueName("q1")?.CallsWaiting ?? 0,
            ByCaller = rig.Manager.GetByChannelId(caller) is null,
        }.Should().BeEquivalentTo(
            new { returned = true, Last = nameof(CallReleasedEvent), QueuedAfterRelease = false, Waiting = 0, ByCaller = true },
            "b's queue join for a's call, parked after it wrote the visit and let go as a's release starts walking, "
            + "cannot announce the call after its release: the release is the last notification, the queue counts no "
            + "caller waiting, and the caller's channel finds nothing. " + rig.Describe());
    }

    /// <summary>
    /// The measure that pins the in-lock raise whatever the scheduling: a synchronous subscriber records, on each notification of the call,
    /// whether the delivering thread holds the call's lock. False whenever a raise is outside the lock, whatever the
    /// scheduling: red on the base for the start and the queueing.
    /// </summary>
    [Theory]
    [InlineData("end")]
    [InlineData("release")]
    public async Task CallNotifications_ShouldBeDeliveredUnderTheCallsLock_WhenASubscriberReceivesThem(string run)
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        CallSession? call = null;
        var records = new List<(string Kind, bool Held)>();
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallStartedEvent && call is null)
                call = rig.Manager.GetById(e.SessionId);
            if (call is null || e.SessionId != call.SessionId)
                return;
            if (e is CallStartedEvent or CallQueuedEvent or CallConnectedEvent or CallEndedEvent or CallReleasedEvent)
                records.Add((e.GetType().Name, call.SyncRoot.IsHeldByCurrentThread));
        }));

        var a = rig["a"];
        string[] expectedKinds;
        if (run == "end")
        {
            a.Deliver(QueueCallConnected("q1", "lk1", "lkm1", "PJSIP/agent1"));
            a.Deliver(MemberHangsUp("q1", "lk1", "lkm1", "PJSIP/agent1"));
            expectedKinds = [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallConnectedEvent), nameof(CallEndedEvent)];
        }
        else
        {
            a.Deliver(NewChannel("lk2", CallerChannel("lk2"), "Ring", "lk2", context: "from-trunk", exten: "500"),
                Join("q1", CallerChannel("lk2"), "lk2", "lk2"));
            rig.Release("a");
            expectedKinds = [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallReleasedEvent)];
        }

        records.Should().BeEquivalentTo(expectedKinds.Select(k => (k, true)), options => options.WithStrictOrdering(),
            $"every notification of the call ({run} run) is delivered while the call's lock is held by the delivering "
            + "thread. " + rig.Describe());
    }

    [Fact]
    public async Task CallStartedEvent_ShouldNotFollowCallReleasedEvent_WhenTheArrivalIsLetGoPastTheBound()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var leg = ReleaseRig.CallerLeg("ap");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ArrivalPublished, "a", leg);
        var arrival = ReleaseRig.OnThread(() => rig.Arrival("a", "ap"));
        parker.WaitUntilParked(ReleaseRig.Bound);
        var session = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("ap"))!;

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        parker.LetGo();
        var arrivalDone = ReleaseRig.Returns(arrival);

        var kinds = rig.KindsOf(session.SessionId);
        new
        {
            returned,
            arrivalDone,
            Released = ReleaseRig.ResultOf(release)?.Any(c => c.SessionId == session.SessionId),
            StartedAfterRelease = kinds.Contains(nameof(CallReleasedEvent))
                && kinds.SkipWhile(k => k != nameof(CallReleasedEvent)).Contains(nameof(CallStartedEvent)),
            ByChannel = rig.Manager.GetByChannelId(leg) is null,
            Held = rig.Manager.GetById(session.SessionId) is not null,
        }.Should().BeEquivalentTo(
            new { returned = true, arrivalDone = true, Released = (bool?)true, StartedAfterRelease = false, ByChannel = true, Held = false },
            "an arrival parked after publishing its call and released past the limit announces nothing once let go: "
            + "no call-started after the release, no channel entry, the call not held. " + rig.Describe());
    }

    // --- (c) The release's waits are bounded; a subscriber that throws ----------------

    [Fact]
    public async Task DetachFromServer_ShouldReturnWithinTheBound_WhenAHandlerHoldsACallLockPastIt()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var c1 = rig.ConnectedCall("a", "b1");
        var c2 = rig.ConnectedCall("a", "b2");
        using var blocked = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallHeldEvent && e.SessionId == c1.SessionId)
            {
                blocked.Set();
                gate.Wait(ReleaseRig.Bound);
            }
        }));

        var hold = ReleaseRig.OnThread(() => rig["a"].Server.Channels.OnHold(ReleaseRig.CallerLeg("b1"), "default"));
        try
        {
            blocked.Wait(ReleaseRig.Bound).Should().BeTrue("premise: the hold handler is blocked in its subscriber");
            var release = ReleaseRig.OnThread(() => rig.Release("a"));
            var returnedWithin = ReleaseRig.Returns(release, TimeSpan.FromSeconds(8));
            var whileBlocked = new
            {
                returnedWithin,
                Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
                C1Notified = rig.Released.Count(r => r.SessionId == c1.SessionId),
                C1FoundById = ReferenceEquals(rig.Manager.GetById(c1.SessionId), c1),
                Errors = CallSessionManagerDetachReleaseWindowTests.ErrorsNaming(rig, "a", inFlight: 1, skipped: c1.SessionId),
            };
            gate.Set();
            var holdDone = ReleaseRig.Returns(hold);
            var afterGate = new { c1.State, Held = ReferenceEquals(rig.Manager.GetById(c1.SessionId), c1), Ended = rig.KindsOf(c1.SessionId).Contains(nameof(CallEndedEvent)) };
            var again = rig.Release("a");

            new
            {
                WhileBlocked = whileBlocked,
                holdDone,
                AfterGate = afterGate,
                Again = again.Select(c => c.SessionId).ToArray(),
                C1NotifiedInTotal = rig.Released.Count(r => r.SessionId == c1.SessionId),
                C1Forgotten = rig.Manager.GetById(c1.SessionId) is null,
            }.Should().BeEquivalentTo(
                new
                {
                    WhileBlocked = new
                    {
                        returnedWithin = true, Released = new[] { c2.SessionId }, C1Notified = 0, C1FoundById = true,
                        Errors = 1,
                    },
                    holdDone = true,
                    AfterGate = new { State = CallSessionState.OnHold, Held = true, Ended = false },
                    Again = new[] { c1.SessionId },
                    C1NotifiedInTotal = 1,
                    C1Forgotten = true,
                },
                "a release that cannot take C1's lock (its hold handler is blocked in a subscriber) returns within its "
                + "limits with C2 only, logs one Error naming a, one handler in flight and C1; C1 stays held, on hold "
                + "and not ended, and a later release takes it. " + rig.Describe());
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task DetachFromServer_ShouldReleaseEveryCallAndReturn_WhenACallReleasedSubscriberThrows()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var c1 = rig.ConnectedCall("a", "t1");
        var c2 = rig.ConnectedCall("a", "t2");
        using var faults = new SubscriberFaultCounter();
        using var thrower = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == c1.SessionId)
                throw new InvalidOperationException("a subscriber failed on the release of C1");
        }));
        var second = new List<string>();
        using var recorder = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent)
                second.Add(e.SessionId);
        }));

        Exception? escaped = null;
        IReadOnlyList<CallSession>? released = null;
        try
        {
            released = rig.Release("a");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        var fresh = await rig.NewInstanceAsync("a");
        var attach = ReleaseRig.OnThread(() => rig.Manager.AttachToServer(fresh.Server, "a"));
        var attachReturned = ReleaseRig.Returns(attach, ReleaseRig.NotReturnedWithin) && !attach.IsFaulted;
        var further = rig.Release("a");

        new
        {
            Escaped = escaped?.GetType().Name,
            Released = released?.Select(c => c.SessionId).ToArray(),
            Warnings = rig.Log.At(LogLevel.Warning).Count(w => Equals(w.Values.GetValueOrDefault("EventType"), nameof(CallReleasedEvent))
                && Equals(w.Values.GetValueOrDefault("SessionId"), c1.SessionId) && w.Exception is InvalidOperationException),
            Faults = faults.Total,
            Second = second.ToArray(),
            Forgotten = rig.Manager.GetById(c1.SessionId) is null && rig.Manager.GetById(c2.SessionId) is null,
            attachReturned,
            Further = further.Count,
        }.Should().BeEquivalentTo(
            new
            {
                Escaped = default(string?),
                Released = new[] { c1.SessionId, c2.SessionId },
                Warnings = 1,
                Faults = 1L,
                Second = new[] { c1.SessionId, c2.SessionId },
                Forgotten = true,
                attachReturned = true,
                Further = 0,
            },
            options => options.WithoutStrictOrderingFor(x => x.Released).WithoutStrictOrderingFor(x => x.Second),
            "a subscriber that throws on C1's release is reported once (Warning, counter) and stops nothing: the detach "
            + "returns both calls, the next subscriber is told of both, both are forgotten, and a later attach and "
            + "release of a do not wait. " + rig.Describe());
    }

    /// <summary>
    /// What pins the walk's <c>finally</c>s for the SDK's own exceptions: an exception the subscriber guard
    /// does not catch — <see cref="OutOfMemoryException"/>, the one the guard lets through — raised from inside the
    /// release notification stands for the SDK's own exceptions inside the per-call <c>try</c>. It reaches the caller of
    /// the detach, but the call it was raised for has still left every lookup and the release in progress has ended,
    /// so a later attach of the id does not wait for a walk that died.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldForgetTheCallAndEndTheRelease_WhenAnExceptionTheGuardLetsThroughEscapesTheNotification()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var c1 = rig.ConnectedCall("a", "u1");
        using var thrower = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == c1.SessionId)
            {
                // The one exception type the subscriber guard lets through, raised on purpose to stand for the SDK's own.
#pragma warning disable CA2201, MA0012
                throw new OutOfMemoryException("stands for an exception of the SDK's own inside the walk");
#pragma warning restore CA2201, MA0012
            }
        }));

        Exception? escaped = null;
        try
        {
            rig.Release("a");
        }
        catch (OutOfMemoryException ex)
        {
            escaped = ex;
        }

        var fresh = await rig.NewInstanceAsync("a");
        var attach = ReleaseRig.OnThread(() => rig.Manager.AttachToServer(fresh.Server, "a"));
        var attachReturned = ReleaseRig.Returns(attach, ReleaseRig.NotReturnedWithin) && !attach.IsFaulted;

        new
        {
            Escaped = escaped is not null,
            ById = rig.Manager.GetById(c1.SessionId) is null,
            ByLinkedId = rig.Manager.GetByLinkedId(c1.LinkedId) is null,
            ByChannel = rig.Manager.GetByChannelId(ReleaseRig.CallerLeg("u1")) is null,
            attachReturned,
        }.Should().BeEquivalentTo(
            new { Escaped = true, ById = true, ByLinkedId = true, ByChannel = true, attachReturned = true },
            "an exception escaping the release notification still leaves the call out of every lookup and ends the "
            + "release in progress. " + rig.Describe());
    }

    // --- (d) A refusal from inside a notification, handled or let escape --------------------------------

    [Fact]
    public Task DetachFromServer_ShouldRefuseFromInsideANotification_AndTheDeliveryContinues_WhenTheSubscriberHandlesTheRefusal() =>
        RefusalFromInsideANotification(letsItEscape: false);

    [Fact]
    public Task DetachFromServer_ShouldRefuseFromInsideANotification_AndTheDeliveryContinues_WhenTheSubscriberLetsTheRefusalEscape() =>
        RefusalFromInsideANotification(letsItEscape: true);

    private static async Task RefusalFromInsideANotification(bool letsItEscape)
    {
        await using var rig = await ReleaseRig.StartAsync("srv-a");
        var c1 = rig.ConnectedCall("srv-a", "r1");
        var c2 = rig.ConnectedCall("srv-a", "r2");
        using var faults = new SubscriberFaultCounter();
        Exception? refusal = null;
        using var first = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is not CallEndedEvent || e.SessionId != c1.SessionId)
                return;
            if (letsItEscape)
            {
                rig.Manager.DetachFromServer("srv-a", DetachedCalls.Release);
                return;
            }

            try
            {
                rig.Manager.DetachFromServer("srv-a", DetachedCalls.Release);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                refusal = ex;
            }
        }));
        var secondGotEnded = 0;
        using var second = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallEndedEvent && e.SessionId == c1.SessionId)
                secondGotEnded++;
        }));

        Exception? atTheRoute = null;
        try
        {
            rig.HangUpCall("srv-a", "r1");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            atTheRoute = ex;
        }

        rig["srv-a"].Server.Channels.OnHold(ReleaseRig.CallerLeg("r2"), "default");
        var warnings = rig.Log.At(LogLevel.Warning).Where(w =>
            Equals(w.Values.GetValueOrDefault("EventType"), nameof(CallEndedEvent))
            && Equals(w.Values.GetValueOrDefault("SessionId"), c1.SessionId)
            && w.Exception is InvalidOperationException).ToArray();

        new
        {
            AtTheRoute = atTheRoute?.GetType().Name,
            Refusal = (letsItEscape ? warnings.FirstOrDefault()?.Exception : refusal)?.GetType().Name,
            RefusalNamesA = (letsItEscape ? warnings.FirstOrDefault()?.Exception : refusal)?.Message
                .Contains("srv-a", StringComparison.Ordinal) == true,
            Warnings = warnings.Length,
            Faults = faults.Total,
            AAttached = c2.State == CallSessionState.OnHold,
            C2Held = ReferenceEquals(rig.Manager.GetById(c2.SessionId), c2),
            C1Ended = c1.State is CallSessionState.Completed or CallSessionState.Failed,
            secondGotEnded,
            ByChannel = rig.Manager.GetByChannelId(ReleaseRig.CallerLeg("r1")) is null,
        }.Should().BeEquivalentTo(
            new
            {
                AtTheRoute = default(string?),
                Refusal = nameof(InvalidOperationException),
                RefusalNamesA = true,
                Warnings = letsItEscape ? 1 : 0,
                Faults = letsItEscape ? 1L : 0L,
                AAttached = true,
                C2Held = true,
                C1Ended = true,
                secondGotEnded = 1,
                ByChannel = true,
            },
            $"a release requested from inside C1's call-ended notification is refused naming srv-a ({(letsItEscape ? "let escape: reported once as a subscriber failure" : "handled by the subscriber")}); "
            + "a stays attached, C2 held, C1 ends, the next subscriber is told, the channel entry goes, and the route "
            + "that reported the hangup sees no exception. " + rig.Describe());
    }
}

using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class ExistingCallAdmissionConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitExistingCalls_ShouldLoseItsActualClaim_WhenRegistrationWinsAfterItsLookup(bool existingTarget)
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Attach();
        if (existingTarget) rig.Add("Z");
        var target = rig.Manager.GetByLinkedId("server", "L");
        var beforeEvents = target?.Events.Count;
        var beforeSaves = target is null ? 0 : rig.Store.Saves.Count(s => s.Id == target.SessionId);
        using var lookup = new NotificationGate();
        using var candidate = new NotificationGate();
        var candidateId = "";
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (id != "X") return;
            if (step == HandlerStep.AdmissionNoOwner) lookup.Hold();
            if (!existingTarget && step == HandlerStep.AdmissionCandidateHeld)
            {
                candidateId = rig.Manager.ActiveSessions.Single(c => c.LinkedId == "L").SessionId;
                candidate.Hold();
            }
        };
        var winner = new CallSession("winner", "R", "server", CallDirection.Inbound);
        winner.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP", Role = ParticipantRole.Caller });
        var workers = new List<Task>();
        await NotificationGate.RunAsync([lookup, candidate], workers, async () =>
        {
            var admission = rig.Manager.AdmitExistingCalls("server").AsTask(); workers.Add(admission);
            await lookup.Entered.Task.WaitAsync(Run.Bound);
            rig.Manager.RegisterReconstructedSession(winner).Should().BeTrue();
            rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(winner, "registration must win the actual claim before admission resumes");
            lookup.Release();
            if (!existingTarget)
            {
                await candidate.Entered.Task.WaitAsync(Run.Bound);
                rig.Manager.GetByLinkedId("server", "L").Should().BeNull("a provisional loser must not be joinable");
                rig.Add("Y");
                target = rig.Manager.GetByChannelId("server", "Y");
                target.Should().NotBeNull();
                target!.SessionId.Should().NotBe(candidateId);
                candidate.Release();
            }
            await admission.WaitAsync(Run.Bound);
        });
        rig.Manager.StepHook = null;
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(winner);
        target!.Participants.Select(p => p.UniqueId).Should().Equal(existingTarget ? "Z" : "Y");
        if (existingTarget)
        {
            target.Events.Count.Should().Be(beforeEvents);
            rig.Store.Saves.Count(s => s.Id == target.SessionId).Should().Be(beforeSaves);
        }
        else
        {
            rig.Manager.GetById(candidateId).Should().BeNull();
            rig.Events.Should().NotContain(e => e.SessionId == candidateId);
            rig.Store.Saves.Should().NotContain(s => s.Id == candidateId);
        }
        rig.Hangup("X");
        rig.Ends(winner).Should().Be(1);
        rig.Events.OfType<CallStartedEvent>().Should().ContainSingle();
        rig.AssertIndexes();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdmitExistingCalls_ShouldPreserveTheChannelOwner_WhenTheReturningTableRelabelsIt(bool admit, bool otherCall)
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y"); rig.Attach();
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Manager.DetachFromServer("server");
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel("X", "PJSIP/X", "L-prime"), new StatusChannel("Y", "PJSIP/Y", "L-prime")] };
        await using var replacement = await Run.StartAsync(peer);
        rig.Manager.AttachToServer(replacement.Server, "server");
        CallSession? other = null;
        if (otherCall)
        {
            replacement.Server.Channels.OnNewChannel("Z", "PJSIP/Z", ChannelState.Up, linkedId: "L-prime");
            other = rig.Manager.GetByLinkedId("server", "L-prime");
        }
        var starts = rig.Events.OfType<CallStartedEvent>().Count();
        var saves = rig.Store.Saves.Count;
        var otherEvents = other?.Events.Count;
        if (admit) await rig.Manager.AdmitExistingCalls("server");
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
        rig.Manager.GetByChannelId("server", "Y").Should().BeSameAs(call);
        rig.Events.OfType<CallStartedEvent>().Count().Should().Be(starts);
        rig.Store.Saves.Count.Should().Be(saves);
        if (other is not null)
        {
            other.Participants.Select(p => p.UniqueId).Should().Equal("Z");
            other.Events.Count.Should().Be(otherEvents);
        }
        replacement.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
        rig.Ends(call).Should().Be(0);
        replacement.Server.Channels.OnHangup("Y", HangupCause.UserBusy);
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        rig.AssertIndexes();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitExistingCalls_ShouldKeepOneCall_WhenItsFirstVisitedLegHangsUpBeforeTheSecondVisit(bool reconstructed)
    {
        await using var rig = await AdmissionRig.CreateAsync();
        CallSession? original = null;
        if (reconstructed)
        {
            original = new CallSession("restored", "L", "server", CallDirection.Inbound);
            rig.Manager.RegisterReconstructedSession(original).Should().BeTrue();
        }
        rig.Add("X"); rig.Add("Y"); rig.Attach();
        using var second = new NotificationGate();
        var visits = 0; var firstId = ""; var lastId = "";
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step != HandlerStep.AdmissionVisit) return;
            if (Interlocked.Increment(ref visits) == 1) firstId = id;
            else { lastId = id; second.Hold(); }
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([second], workers, async () =>
        {
            var admission = rig.Manager.AdmitExistingCalls("server").AsTask(); workers.Add(admission);
            await second.Entered.Task.WaitAsync(Run.Bound);
            original ??= rig.Manager.GetByLinkedId("server", "L");
            original.Should().NotBeNull();
            rig.Hangup(firstId);
            rig.Ends(original!).Should().Be(0);
            second.Release(); await admission.WaitAsync(Run.Bound);
        });
        rig.Manager.StepHook = null;
        rig.Manager.GetByChannelId("server", lastId).Should().BeSameAs(original);
        rig.Hangup(lastId, HangupCause.UserBusy);
        rig.Ends(original!).Should().Be(1);
        rig.Events.OfType<CallStartedEvent>().Count().Should().Be(reconstructed ? 0 : 1);
        rig.AssertIndexes();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitExistingCalls_ShouldLeaveNoOpenCall_WhenItsUnvisitedChannelsHangUp(bool admit)
    {
        await using var rig = await AdmissionRig.CreateAsync();
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Add("X"); rig.Add("Y"); rig.Attach(); rig.Hangup("X");
        if (admit)
        {
            using var walk = new NotificationGate();
            rig.Manager.StepHook = (step, _, _) => { if (step == HandlerStep.AdmissionWalkStarted) walk.Hold(); };
            var workers = new List<Task>();
            await NotificationGate.RunAsync([walk], workers, async () =>
            {
                var admission = rig.Manager.AdmitExistingCalls("server").AsTask(); workers.Add(admission);
                await walk.Entered.Task.WaitAsync(Run.Bound);
                rig.Hangup("Y", HangupCause.UserBusy);
                walk.Release(); await admission.WaitAsync(Run.Bound);
            });
            rig.Manager.StepHook = null;
        }
        else rig.Hangup("Y", HangupCause.UserBusy);
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        call.Metadata.Should().NotContainKey("cause");
        rig.Events.OfType<CallStartedEvent>().Should().BeEmpty();
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task AdmitExistingCalls_ShouldStopDuringTheSnapshotWait_WhenItsAttachmentIsDetached(bool release, bool sameInstance, bool replace)
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task, StatusChannels = [new StatusChannel("X", "PJSIP/X", "L")] };
        await using var rig = await AdmissionRig.CreateAsync(complete: false, peer: peer);
        await using var replacement = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel("Z", "PJSIP/Z", "K")] });
        rig.Add("X"); rig.Attach();
        var oldSnapshotDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Run.Server.ChannelSnapshotCompleted += _ => oldSnapshotDone.TrySetResult();
        var admission = rig.Manager.AdmitExistingCalls("server").AsTask();
        var stoppedBeforeAnswer = false;
        try
        {
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            rig.Manager.DetachFromServer("server", release ? DetachedCalls.Release : DetachedCalls.Keep);
            if (replace) rig.Manager.AttachToServer(sameInstance ? rig.Run.Server : replacement.Server, "server");
            // fence-allow: GUARD-TIMEOUT — the detached admission must stop before the deliberately unanswered Status.
            stoppedBeforeAnswer = await Task.WhenAny(admission, Task.Delay(TimeSpan.FromSeconds(3))) == admission;
            if (stoppedBeforeAnswer) await admission;
            rig.Events.OfType<CallStartedEvent>().Should().BeEmpty();
        }
        finally
        {
            answer.TrySetResult();
            await admission.WaitAsync(Run.Bound);
            await oldSnapshotDone.Task.WaitAsync(Run.Bound);
        }
        stoppedBeforeAnswer.Should().BeTrue("detaching ends the captured wait even though Keep leaves the handler gate open");
        if (replace)
        {
            await rig.Manager.AdmitExistingCalls("server");
            rig.Manager.ActiveSessions.Should().ContainSingle();
            rig.Manager.GetByChannelId("server", sameInstance ? "X" : "Z").Should().NotBeNull();
        }
        else rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldKeepAnUnobservedCall_WhenStatusIsPermanentlyRefused()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var rig = await AdmissionRig.CreateAsync(complete: false, peer: peer);
        rig.Add("X"); rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.Should().NotBeNull();
        rig.Run.Server.LastStatusRefusal.Should().NotBeNull();
        rig.Hangup("X");
        rig.Ends(call).Should().Be(0);
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        rig.Manager.DetachFromServer("server", DetachedCalls.Release).Should().ContainSingle().Which.Should().BeSameAs(call);
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }
    [Fact]
    public async Task AdmitExistingCalls_ShouldReturnFromTheSubscriber_WhenAnotherThreadHoldsAChannelLock()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Attach(); rig.Add("X"); rig.Add("Y", "K");
        var call = rig.Manager.GetByChannelId("server", "X")!;
        var channel = rig.Run.Server.Channels.GetByUniqueId("Y")!;
        using var held = new NotificationGate();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? admission = null;
        using var subscription = rig.Manager.Events.Subscribe(new AdmissionObserver(e =>
        {
            if (e is not CallEndedEvent ended || ended.SessionId != call.SessionId) return;
            admission = rig.Manager.AdmitExistingCalls("server").AsTask();
            returned.TrySetResult();
        }));
        var workers = new List<Task>();
        var returnedWhileLocked = false;
        await NotificationGate.RunAsync([held], workers, async () =>
        {
            workers.Add(Task.Run(() => { lock (channel.SyncRoot) held.Hold(); }));
            await held.Entered.Task.WaitAsync(Run.Bound);
            workers.Add(Task.Run(() => rig.Hangup("X")));
            // fence-allow: GUARD-TIMEOUT — a subscriber must return before the walk can obtain this channel lock.
            returnedWhileLocked = await Task.WhenAny(returned.Task, Task.Delay(TimeSpan.FromSeconds(3))) == returned.Task;
            held.Release();
            await Task.WhenAll(workers).WaitAsync(Run.Bound);
            admission.Should().NotBeNull();
            await admission!.WaitAsync(Run.Bound);
        });
        returnedWhileLocked.Should().BeTrue("the public call must offload the walk before it can take a channel lock");
        rig.Ends(call).Should().Be(1);
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldWithdrawItsUnannouncedCandidate_WhenReleaseClosesBeforeTheActualClaim()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Attach();
        using var candidate = new NotificationGate();
        var candidateId = "";
        rig.Manager.StepHook = (step, _, _) =>
        {
            if (step != HandlerStep.AdmissionCandidateHeld) return;
            candidateId = rig.Manager.ActiveSessions.Single().SessionId;
            candidate.Hold();
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([candidate], workers, async () =>
        {
            var admission = rig.Manager.AdmitExistingCalls("server").AsTask(); workers.Add(admission);
            await candidate.Entered.Task.WaitAsync(Run.Bound);
            var release = Task.Run(() => rig.Manager.DetachFromServer("server", DetachedCalls.Release)); workers.Add(release);
            (await release.WaitAsync(Run.Bound)).Should().BeEmpty("the candidate lock stays held through release's bounded walk");
            candidate.Release(); await admission.WaitAsync(Run.Bound);
        });
        rig.Manager.StepHook = null;
        rig.Manager.GetById(candidateId).Should().BeNull();
        rig.Manager.GetByLinkedId("server", "L").Should().BeNull();
        rig.Manager.GetByChannelId("server", "X").Should().BeNull();
        rig.Events.OfType<CallStartedEvent>().Should().BeEmpty();
        rig.Store.Saves.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldStopBeforeAnotherVisit_WhenTheIdIsReattachedDuringTheWalk()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y"); rig.Attach();
        using var second = new NotificationGate();
        var visits = 0; var firstId = ""; var skippedId = "";
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step != HandlerStep.AdmissionVisit) return;
            if (Interlocked.Increment(ref visits) == 1) firstId = id;
            else { skippedId = id; second.Hold(); }
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([second], workers, async () =>
        {
            var admission = rig.Manager.AdmitExistingCalls("server").AsTask(); workers.Add(admission);
            await second.Entered.Task.WaitAsync(Run.Bound);
            var original = rig.Manager.GetByLinkedId("server", "L")!;
            rig.Manager.DetachFromServer("server");
            var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [
                new StatusChannel(firstId, "PJSIP/" + firstId, "L"), new StatusChannel("Z", "PJSIP/Z", "K")] };
            await using var replacement = await Run.StartAsync(peer);
            rig.Manager.AttachToServer(replacement.Server, "server");
            second.Release(); await admission.WaitAsync(Run.Bound);
            rig.Manager.StepHook = null;
            rig.Manager.GetById(original.SessionId).Should().BeSameAs(original);
            rig.Manager.GetByChannelId("server", skippedId).Should().BeNull();
            rig.Manager.GetByChannelId("server", "Z").Should().BeNull();
            rig.Events.OfType<CallStartedEvent>().Should().ContainSingle();
            rig.Log.Entries.Should().Contain(e => e.Message.Contains("stopped because its attachment changed: True", StringComparison.Ordinal));
            await rig.Manager.AdmitExistingCalls("server");
            rig.Manager.GetByChannelId("server", "Z").Should().NotBeNull();
            rig.Events.OfType<CallStartedEvent>().Should().HaveCount(2);
            rig.AssertIndexes();
        });
        rig.Manager.StepHook = null;
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldKeepTheReconstructedCall_WhenItsLegIsInTheTableBeforeItsArrivalHandler()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var rig = await AdmissionRig.CreateAsync(complete: false, peer: peer);
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        using var arrival = new NotificationGate();
        rig.Run.Server.Channels.ChannelAdded += _ => arrival.Hold();
        rig.Attach();
        var workers = new List<Task>();
        await NotificationGate.RunAsync([arrival], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Add("X")));
            await arrival.Entered.Task.WaitAsync(Run.Bound);
            await rig.Manager.AdmitExistingCalls("server").AsTask().WaitAsync(Run.Bound);
            rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
            rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
            rig.Ends(call).Should().Be(0);
            rig.Events.OfType<CallStartedEvent>().Should().BeEmpty();
            arrival.Release();
            await workers[0].WaitAsync(Run.Bound);
        });
        call.Participants.Should().ContainSingle();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldWaitForTheSnapshot_WhenItsAttachedTableIsIncompleteAndNotRefused()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var rig = await AdmissionRig.CreateAsync(complete: false, peer: peer);
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Attach();
        var requested = peer.AskedAtLeast("Status", 1);
        var admission = rig.Manager.AdmitExistingCalls("server").AsTask();
        var requestedBeforeReturn = false;
        try
        {
            requestedBeforeReturn = ReferenceEquals(await Task.WhenAny(requested, admission).WaitAsync(Run.Bound), requested);
            rig.Ends(call).Should().Be(0);
            rig.Manager.IsPendingDecision(call).Should().BeTrue();
            rig.Run.Server.LastStatusRefusal.Should().BeNull();
            if (requestedBeforeReturn) admission.IsCompleted.Should().BeFalse("the peer has not answered the requested snapshot");
        }
        finally
        {
            answer.TrySetResult();
            await admission.WaitAsync(Run.Bound);
        }
        requestedBeforeReturn.Should().BeTrue("an incomplete attached table requires an actual snapshot before admission returns");
        rig.Ends(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        rig.Run.Server.LastStatusRefusal.Should().BeNull();
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldKeepTheLeglessCall_WhenItsSnapshotIsRefusedUntilALaterCompletion()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var rig = await AdmissionRig.CreateAsync(complete: false, peer: peer);
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue(); rig.Attach();
        await rig.Manager.AdmitExistingCalls("server");
        rig.Run.Server.LastStatusRefusal.Should().NotBeNull();
        rig.Ends(call).Should().Be(0);
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        peer.StatusRefusedFromAsk = 0;
        await rig.Run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        rig.Ends(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        rig.Run.Server.LastStatusRefusal.Should().BeNull();
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldCompleteWithoutWaitingInTheSubscriber_WhenAnArrivalOfThatServerIsInFlight()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Attach(); rig.Add("X");
        var ended = rig.Manager.GetByChannelId("server", "X")!;
        using var subscriber = new NotificationGate();
        using var arrival = new NotificationGate();
        using var walk = new NotificationGate();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? admission = null;
        using var subscription = rig.Manager.Events.Subscribe(new AdmissionObserver(e =>
        {
            if (e is not CallEndedEvent ending || ending.SessionId != ended.SessionId) return;
            subscriber.Hold();
            admission = rig.Manager.AdmitExistingCalls("server").AsTask();
            returned.TrySetResult();
        }));
        // The walk parks at its first step on whichever thread runs it. Offloaded, the subscriber has already
        // returned; on the subscriber's own thread the park sits inside it and `returned` cannot complete.
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.AdmissionWalkStarted) walk.Hold();
            if (step == HandlerStep.ArrivalBuilt && id == "Y") arrival.Hold();
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([subscriber, arrival, walk], workers, async () =>
        {
            var hangup = Task.Run(() => rig.Hangup("X")); workers.Add(hangup);
            await subscriber.Entered.Task.WaitAsync(Run.Bound);
            var added = Task.Run(() => rig.Add("Y", "K")); workers.Add(added);
            await arrival.Entered.Task.WaitAsync(Run.Bound);
            subscriber.Release();
            await walk.Entered.Task.WaitAsync(Run.Bound);
            await returned.Task.WaitAsync(Run.Bound);
            await hangup.WaitAsync(Run.Bound);
            walk.Release(); arrival.Release(); await added.WaitAsync(Run.Bound);
            admission.Should().NotBeNull();
            await admission!.WaitAsync(Run.Bound);
        });
        rig.Manager.StepHook = null;
        rig.Ends(ended).Should().Be(1);
        rig.Manager.GetByChannelId("server", "Y").Should().NotBeNull();
        rig.Events.OfType<CallStartedEvent>().Should().HaveCount(2);
        rig.AssertIndexes();
    }

}

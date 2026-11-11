using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The loss mark of a session notification raised from a channel event another thread posted and did not drain: the
/// channel manager's outbox lets a drain whose turn wait ran out raise it (<see cref="LiveEventOutbox"/>). An arrival
/// admitted from a reload's snapshot keeps the request mark its channel carries, whichever drain raises it; a live
/// arrival whose dispatch is parked keeps its own event's mark when a reconciliation's drain adopts it; and a reload's
/// departure adopted by a later dispatch is stamped with that dispatch's mark, a closing no earlier than its content.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>. The poster is parked in the
/// outbox's <see cref="LiveEventOutbox.Posted"/> hook, right after its post and before its drain, with only the posted
/// channel's lock held, which neither another channel's admission nor the session engine takes.
/// </para>
/// <para>
/// Gate-sequenced: the park sets its entered signal before it blocks and the test waits on it; the adopting drain runs
/// out the outbox's own turn wait (<see cref="LiveEventOutbox.DefaultTurnWait"/>, one second), which is the path under
/// test, not a wait of the test's. Every wait is bounded by <see cref="Run.Bound"/>.
/// </para>
/// </remarks>
public sealed class SessionEventEpochClaimTests
{
    private const string ServerId = "srv-epoch";
    private const string Caller = "1700000600.1";
    private const string CallerChannel = "PJSIP/pstn-00000601";
    private const string Other = "1700000600.2";
    private const string OtherChannel = "PJSIP/pstn-00000602";

    [Fact]
    public async Task ReloadAdmittedCallStarted_ShouldCarryTheRequestMark_WhenItsEntryIsClaimedByAnotherDrain()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        var requestMark = run.Server.ReadSessionLossMark();

        using var park = new PosterPark(c => c is { UniqueId: Caller, AdmittedFromSnapshot: true });
        run.Server.Channels.Events.Posted = park.OnPosted;
        peer.StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)];
        var reconciliation = Task.Run(() => run.Server.ReconcileChannelsAsync().AsTask());
        try
        {
            await park.Entered.WaitAsync(Run.Bound);
            await EpochRig.DropOneEventAsync(run, peer);
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
            var other = await recorder.WaitAsync<CallStartedEvent>(
                e => e.SessionId == manager.GetByChannelId(ServerId, Other)?.SessionId);
            var started = recorder.Seen.Select(s => s.Notification).OfType<CallStartedEvent>()
                .Single(e => e.SessionId != other.SessionId);
            var parkedWhenAdopted = park.IsParked;
            park.Release();
            await reconciliation.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                parkedWhenAdopted.Should().BeTrue("premise: the reconciliation was still parked after its post when the arrival was raised");
                requestMark.Should().NotBeNull("premise: the server runs over the SDK's connection");
                other.Epoch.Should().NotBeNull("premise: the claiming event was delivered over the SDK's connection");
                other.Epoch?.DroppedBefore.Should().Be(1, "premise: the claiming event was received after the drop");
                started.Epoch.Should().NotBeNull("the call was admitted from the snapshot over the SDK's connection");
                started.Epoch?.DroppedBefore.Should().Be(0,
                    "the snapshot's arrival carries the mark sampled before its request, not the claiming event's");
                started.Epoch?.AmiSession.Should().Be(requestMark?.Session ?? -1, "the request mark's session");
                started.Epoch.Should().NotBe(other.Epoch, "it does not carry the mark of the delivery that raised it");
            }
        }
        finally
        {
            run.Server.Channels.Events.Posted = null;
            park.Release();
            await reconciliation.WaitAsync(Run.Bound);
        }
    }

    [Fact]
    public async Task ClaimedLiveArrival_ShouldCarryItsOwnEventsMark_WhenItsDispatchIsParkedPastTheTurnWait()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);

        // Opened first, so the live channel is newer than the snapshot and the reconciliation keeps it.
        using var window = run.Server.Channels.OpenReadWindow();
        using var park = new PosterPark(c => c is { UniqueId: Caller, AdmittedFromSnapshot: false });
        run.Server.Channels.Events.Posted = park.OnPosted;
        var queued = new QueuedEventSeen("queued-behind-parked");
        using var queuedSubscription = run.Connection.Subscribe(queued);
        var adoption = new AdoptionProbe(manager, Caller, park, queued);
        manager.Events.Subscribe(adoption);
        try
        {
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
            await park.Entered.WaitAsync(Run.Bound);
            await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "queued-behind-parked")]);
            await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "dropped")]);
            // Answered after both, so the connection has read them: one buffered behind the parked dispatch, one refused.
            await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);
            var readerSide = run.Server.ReadCurrentEventLossEpoch();

            // The reload thread's role: a drain that is not a dispatch. It runs out the turn wait and adopts the parked
            // arrival, raising it on this thread.
            var testThread = Environment.CurrentManagedThreadId;
            run.Server.Channels.ReconcileWithSnapshot(
                [new ChannelSnapshotEntry(Other, OtherChannel, ChannelState.Up, LinkedId: Other)], window);
            var started = await recorder.WaitAsync<CallStartedEvent>(
                e => e.SessionId == manager.GetByChannelId(ServerId, Caller)?.SessionId);

            using (new AssertionScope())
            {
                readerSide.DroppedBefore.Should().Be(1, "premise: the reader dropped one event while the arrival was parked");
                adoption.Thread.Should().Be(testThread, "premise: the reconciliation's drain adopted the parked arrival");
                adoption.PosterParked.Should().BeTrue("premise: the poster was alive and parked when its entry was adopted");
                adoption.QueuedDispatched.Should().BeFalse(
                    "premise: the event queued behind the parked dispatch had not been dispatched");
                started.Epoch.Should().NotBeNull("the arrival was delivered over the SDK's connection");
                (started.Epoch?.AmiSession, started.Epoch?.DroppedBefore).Should().Be((1L, 0L),
                    "an adopted live arrival carries its own event's mark, which predates the drop");
            }
        }
        finally
        {
            run.Server.Channels.Events.Posted = null;
            park.Release();
        }
    }

    [Fact]
    public async Task ClaimedReloadDeparture_ShouldCarryTheClaimingDispatchsMark_WhenTheReconciliationIsParkedPastTheTurnWait()
    {
        // A characterization of a documented residual, with nothing to fix: a reload's departure whose reconciliation
        // stalls past the turn wait is raised by a later dispatch's drain and stamped with that dispatch's mark, newer
        // than its content, which a closing allows.
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: 1);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        using var park = new PosterPark(c => c is { UniqueId: Caller, RemovedByReload: true });
        run.Server.Channels.Events.Posted = park.OnPosted;
        var adoption = new EndingProbe(park);
        manager.Events.Subscribe(adoption);
        // The call hung up unseen: the reload's Status no longer lists it.
        var reconciliation = Task.Run(() => run.Server.ReconcileChannelsAsync().AsTask());
        try
        {
            await park.Entered.WaitAsync(Run.Bound);
            await EpochRig.DropOneEventAsync(run, peer);
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
            var ended = await recorder.WaitAsync<CallEndedEvent>();
            var claiming = await recorder.WaitAsync<CallStartedEvent>(e => e.SessionId != started.SessionId);
            park.Release();
            await reconciliation.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                adoption.PosterParked.Should().BeTrue(
                    "premise: the reconciliation was alive and parked after its post when the departure was adopted");
                adoption.Thread.Should().NotBe(park.Thread, "premise: another thread's drain raised the departure");
                started.Epoch?.DroppedBefore.Should().Be(0, "premise: the call started before the drop");
                claiming.Epoch?.DroppedBefore.Should().Be(1, "premise: the claiming event was received after the drop");
                ended.Epoch.Should().NotBeNull("the departure was raised over the SDK's connection");
                ended.Epoch.Should().Be(claiming.Epoch,
                    "the adopted departure is raised inside the claiming event's delivery and carries its mark");
            }
        }
        finally
        {
            run.Server.Channels.Events.Posted = null;
            park.Release();
            await reconciliation.WaitAsync(Run.Bound);
        }
    }

    /// <summary>
    /// Parks the first thread that posts a notification for a channel <paramref name="match"/> accepts, right after its
    /// post and before its drain, until the test releases it.
    /// </summary>
    private sealed class PosterPark(Func<AsteriskChannel, bool> match) : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        private int _taken;
        private int _parked;

        /// <summary>Completes once the poster is parked.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Whether the poster is parked now.</summary>
        public bool IsParked => Volatile.Read(ref _parked) != 0;

        /// <summary>The parked poster's managed thread.</summary>
        public int Thread { get; private set; }

        public void OnPosted(object? first, object? second)
        {
            if (first is not AsteriskChannel channel || !match(channel) || Interlocked.Exchange(ref _taken, 1) != 0)
                return;

            Thread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _parked, 1);
            _entered.TrySetResult();
            try
            {
                _gate.Wait(DispatchGate.HoldBound);
            }
            finally
            {
                Volatile.Write(ref _parked, 0);
            }
        }

        public void Release() => _gate.Set();

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
        }
    }

    /// <summary>Whether the connection has dispatched the <c>UserEvent</c> named <paramref name="name"/>.</summary>
    private sealed class QueuedEventSeen(string name) : IObserver<ManagerEvent>
    {
        private int _seen;

        public bool Seen => Volatile.Read(ref _seen) != 0;

        public void OnNext(ManagerEvent value)
        {
            if (value is UserEventEvent user && user.UserEvent == name)
                Volatile.Write(ref _seen, 1);
        }

        public void OnError(Exception error)
        {
            // The connection never faults its observers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to do.
        }
    }

    /// <summary>What held when the call-started of <paramref name="uniqueId"/>'s call was raised.</summary>
    private sealed class AdoptionProbe(Sessions.Manager.CallSessionManager manager, string uniqueId, PosterPark park,
        QueuedEventSeen queued) : IObserver<SessionDomainEvent>
    {
        public int Thread { get; private set; } = -1;

        public bool PosterParked { get; private set; }

        public bool QueuedDispatched { get; private set; }

        public void OnNext(SessionDomainEvent value)
        {
            if (value is not CallStartedEvent || manager.GetByChannelId(ServerId, uniqueId)?.SessionId != value.SessionId)
                return;

            Thread = Environment.CurrentManagedThreadId;
            PosterParked = park.IsParked;
            QueuedDispatched = queued.Seen;
        }

        public void OnError(Exception error)
        {
            // The engine never faults its subscribers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the engine is disposed; nothing to do.
        }
    }

    /// <summary>What held when the first call ending was raised.</summary>
    private sealed class EndingProbe(PosterPark park) : IObserver<SessionDomainEvent>
    {
        public int Thread { get; private set; } = -1;

        public bool PosterParked { get; private set; }

        public void OnNext(SessionDomainEvent value)
        {
            if (value is not CallEndedEvent || Thread != -1)
                return;

            Thread = Environment.CurrentManagedThreadId;
            PosterParked = park.IsParked;
        }

        public void OnError(Exception error)
        {
            // The engine never faults its subscribers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the engine is disposed; nothing to do.
        }
    }
}

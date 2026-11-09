using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Extensions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Live.Tests.Server;

// Each premise has its own class; the helper contains transport and gate cleanup only.
internal sealed class EndingBaselineRig : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    public Run Run { get; }
    public CallSessionManager Manager { get; } = new(Options.Create(new SessionOptions()),
        NullLogger<CallSessionManager>.Instance, new Store());
    public ConcurrentQueue<SessionDomainEvent> Events { get; } = new();
    private readonly IDisposable _subscription;
    private EndingBaselineRig(Run run)
    {
        Run = run;
        _subscription = Manager.Events.Subscribe(new Observer(Events.Enqueue));
    }
    public static async Task<EndingBaselineRig> CreateAsync(BootingAsterisk? second = null)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        var rig = new EndingBaselineRig(await Run.StartAsync(peer, second, autoReconnect: second is not null));
        try
        {
            await rig.Run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Bound);
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }
    public void Attach() => Manager.AttachToServer(Run.Server, "server");
    public void Add(string id, string linked = "L") => Run.Server.Channels.OnNewChannel(id, "PJSIP/" + id,
        ChannelState.Up, linkedId: linked);
    public void Hangup(string id) => Run.Server.Channels.OnHangup(id, HangupCause.NormalClearing);
    public int Starts(string linked = "L") => Events.OfType<CallStartedEvent>().Count(e => Manager.GetById(e.SessionId)?.LinkedId == linked);
    public int Ends(CallSession call) => Events.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId);
    public string Describe(CallSession call) => $"original={call.SessionId}/{call.State}; participants=[{string.Join(',', call.Participants.Select(p => p.UniqueId))}]; startsL={Starts()}; endingsOriginal={Ends(call)}; activeL={Manager.ActiveSessions.Count(s => s.LinkedId == "L")}; currentL={Manager.GetByLinkedId("L")?.SessionId}; table=[{string.Join(',', Run.Server.Channels.ActiveChannels.Select(c => c.UniqueId))}]; cause={call.Metadata.GetValueOrDefault("cause") ?? "null"}; hangup={call.HangupCause?.ToString() ?? "null"}";
    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await Manager.DisposeAsync();
        await Run.DisposeAsync();
    }
    private sealed class Observer(Action<SessionDomainEvent> action) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => action(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
    private sealed class Store : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}

internal sealed class NotificationGate : IDisposable
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    public void Hold()
    {
        Entered.TrySetResult();
        if (!_release.Wait(EndingBaselineRig.Bound)) throw new TimeoutException("notification gate was not released");
    }
    public void Release() => _release.Set();
    public void Dispose() => _release.Dispose();
    public static async Task RunAsync(NotificationGate[] gates, List<Task> workers, Func<Task> body)
    {
        try { await body(); }
        finally
        {
            foreach (var gate in gates) gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
        }
    }
}

public sealed class IncompleteParticipantEndingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OnHangup_ShouldKeepTheReconstructedCall_WhenAnotherLegIsAlreadyInTheTableButItsArrivalIsHeld()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Add("X");
        rig.Run.FirstPeer.StatusChannels = [new StatusChannel("X", "PJSIP/X", "L")];
        await rig.Run.Server.ReconcileChannelsAsync();
        using var gate = new NotificationGate();
        rig.Run.Server.Channels.ChannelAdded += c => { if (c.UniqueId == "Y") gate.Hold(); };
        rig.Attach();
        var call = new CallSession("reconstructed", "L", "server", CallDirection.Inbound);
        call.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP", Role = ParticipantRole.Caller });
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var workers = new List<Task>();
        var inProgressAfterX = false;
        var joinedOriginal = false;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Add("Y")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            var hangup = Task.Run(() => rig.Hangup("X"));
            workers.Add(hangup);
            await hangup.WaitAsync(EndingBaselineRig.Bound);
            inProgressAfterX = rig.Manager.ActiveSessions.Contains(call);
            output.WriteLine("after X: " + rig.Describe(call));
            gate.Release();
            await workers[0].WaitAsync(EndingBaselineRig.Bound);
            joinedOriginal = ReferenceEquals(rig.Manager.GetByChannelId("Y"), call);
            output.WriteLine("after Y arrival: " + rig.Describe(call));
            rig.Hangup("Y");
        });
        output.WriteLine("final: " + rig.Describe(call));
        new { InProgressAfterX = inProgressAfterX, JoinedOriginal = joinedOriginal, Starts = rig.Starts(), Endings = rig.Ends(call), Cause = call.HangupCause }
            .Should().BeEquivalentTo(new { InProgressAfterX = true, JoinedOriginal = true, Starts = 0, Endings = 1, Cause = (HangupCause?)HangupCause.NormalClearing });
    }
}

public sealed class SnapshotAdmissionEndingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_ShouldKeepOneCall_WhenItsFirstLegHangsUpBeforeItsLastLegIsAdmitted(bool reconnect)
    {
        var second = reconnect ? new BootingAsterisk { BootedAtLogin = true } : null;
        await using var rig = await EndingBaselineRig.CreateAsync(second);
        rig.Attach();
        using var gate = new NotificationGate();
        rig.Run.Server.Channels.ChannelAdded += c => { if (c.UniqueId == "W") gate.Hold(); };
        StatusChannel[] snapshot = [new("X", "PJSIP/X", "L"), new("W", "PJSIP/W", "other"), new("Y", "PJSIP/Y", "L")];
        var workers = new List<Task>();
        CallSession? original = null;
        var absentY = false;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            if (reconnect)
            {
                second!.StatusChannels = snapshot;
                rig.Run.FirstPeer.CloseSession();
                await rig.Run.SecondSocketCreated.WaitAsync(EndingBaselineRig.Bound);
                rig.Run.ReleaseSecondPeer();
                workers.Add(rig.Run.ServerLog.Logged("[LIVE] State loaded", 2));
            }
            else
            {
                rig.Run.FirstPeer.StatusChannels = snapshot;
                workers.Add(Task.Run(async () => await rig.Run.Server.ReconcileChannelsAsync()));
            }
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            original = rig.Manager.GetByLinkedId("L")!;
            var hangup = Task.Run(() => rig.Hangup("X"));
            workers.Add(hangup);
            await hangup.WaitAsync(EndingBaselineRig.Bound);
            absentY = rig.Run.Server.Channels.GetByUniqueId("Y") is null;
            output.WriteLine($"reconnect={reconnect}; before release; absentY={absentY}; " + rig.Describe(original));
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
            output.WriteLine("after snapshot: " + rig.Describe(original));
            rig.Run.Server.Channels.GetByUniqueId("Y").Should().NotBeNull();
            rig.Hangup("Y");
        });
        output.WriteLine("final: " + rig.Describe(original!));
        new { AbsentY = absentY, Starts = rig.Starts(), OriginalEndings = rig.Ends(original!), OriginalOwnsY = original!.Participants.Any(p => p.UniqueId == "Y"), Active = rig.Manager.ActiveSessions.Count(s => s.LinkedId == "L") }
            .Should().BeEquivalentTo(new { AbsentY = true, Starts = 1, OriginalEndings = 1, OriginalOwnsY = true, Active = 0 });
    }
}

public sealed class ConcurrentDepartureEndingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnChannelRemoved_ShouldEndExactlyOnceBeforeTheFirstAnnouncementReturns_WhenTheOtherDepartureFinishes(bool hangupFirst)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach(); rig.Add("X"); rig.Add("Y");
        var call = rig.Manager.GetByLinkedId("L")!;
        using var gate = new NotificationGate();
        rig.Run.Server.Channels.ChannelRemoved += c => { if (c.UniqueId == (hangupFirst ? "Y" : "X")) gate.Hold(); };
        using var window = rig.Run.Server.Channels.OpenReadWindow();
        var workers = new List<Task>();
        var endingsBeforeRelease = -1;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() =>
            {
                if (hangupFirst) rig.Hangup("Y");
                else rig.Run.Server.Channels.ReconcileWithSnapshot([new ChannelSnapshotEntry("Y", "PJSIP/Y", ChannelState.Up, LinkedId: "L")], window);
            }));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            workers.Add(Task.Run(() =>
            {
                if (hangupFirst) rig.Run.Server.Channels.ReconcileWithSnapshot([], window);
                else rig.Hangup("Y");
            }));
            await workers[1].WaitAsync(EndingBaselineRig.Bound);
            endingsBeforeRelease = rig.Ends(call);
            output.WriteLine($"hangupFirst={hangupFirst}; before release: " + rig.Describe(call));
        });
        output.WriteLine("final: " + rig.Describe(call));
        endingsBeforeRelease.Should().Be(1);
        rig.Ends(call).Should().Be(1);
        rig.Manager.ActiveSessions.Should().NotContain(call);
        call.Metadata.GetValueOrDefault("cause").Should().Be(hangupFirst ? "reload" : null);
        call.HangupCause.Should().Be(hangupFirst ? null : HangupCause.NormalClearing);
    }
}

[Trait("Category", "Spike")]
public sealed class DelayedArrivalEndingResidualTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OnChannelAdded_ShouldNotReopenTheCall_WhenItsRemovalAlreadyReachedTheEngine()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        using var added = new NotificationGate();
        using var removed = new NotificationGate();
        rig.Run.Server.Channels.ChannelAdded += c => { if (c.UniqueId == "X") added.Hold(); };
        rig.Attach(); rig.Add("W");
        var original = rig.Manager.GetByLinkedId("L")!;
        rig.Run.Server.Channels.ChannelRemoved += c => { if (c.UniqueId == "X") removed.Hold(); };
        var workers = new List<Task>();
        var lateParticipant = false;
        var replacement = false;
        var leakedAfterComplete = false;
        await NotificationGate.RunAsync([added, removed], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Add("X")));
            await added.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await removed.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            var hangup = Task.Run(() => rig.Hangup("W"));
            workers.Add(hangup);
            await hangup.WaitAsync(EndingBaselineRig.Bound);
            output.WriteLine("removal handled, arrival held: " + rig.Describe(original));
            added.Release();
            await workers[0].WaitAsync(EndingBaselineRig.Bound);
            var late = rig.Manager.GetByChannelId("X");
            lateParticipant = late is not null;
            replacement = late is not null && !ReferenceEquals(late, original);
            output.WriteLine("late arrival, removal still held: " + rig.Describe(original));
            await rig.Run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
            leakedAfterComplete = rig.Manager.ActiveSessions.Any(s => s.LinkedId == "L");
            output.WriteLine("after explicit empty complete snapshot: " + rig.Describe(original));
        });
        new { LateParticipant = lateParticipant, Replacement = replacement, LeakedAfterComplete = leakedAfterComplete, Starts = rig.Starts() }
            .Should().BeEquivalentTo(new { LateParticipant = false, Replacement = false, LeakedAfterComplete = false, Starts = 1 }, "desirable residual safety; this change does not promise to fix the base residual");
    }
}

public sealed class ChannelNotificationBoundaryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelAdded_ShouldRunAfterInsertionOutsideBothLocks_WhenAdmissionIsLiveOrSnapshot(bool snapshot)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        var channels = rig.Run.Server.Channels;
        var callbackCount = 0;
        channels.ChannelAdded += channel =>
        {
            if (channel.UniqueId != "X") return;
            channels.GetByUniqueId("X").Should().BeSameAs(channel);
            channel.SyncRoot.IsHeldByCurrentThread.Should().BeFalse();
            Task.Run(() => rig.Add("probe", "probe")).WaitAsync(EndingBaselineRig.Bound).GetAwaiter().GetResult();
            Interlocked.Increment(ref callbackCount);
        };
        if (snapshot)
        {
            using var window = channels.OpenReadWindow();
            channels.ReconcileWithSnapshot([new ChannelSnapshotEntry("X", "PJSIP/X", ChannelState.Up, LinkedId: "L")], window);
        }
        else rig.Add("X");
        callbackCount.Should().Be(1);
        output.WriteLine($"snapshot={snapshot}; insertion visible, channel lock free, independent manager mutation completed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelRemoved_ShouldRunAfterRemovalOutsideBothLocks_WhenDepartureIsHangupOrSnapshot(bool snapshot)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach(); rig.Add("X");
        var channels = rig.Run.Server.Channels;
        var callbackCount = 0;
        channels.ChannelRemoved += channel =>
        {
            if (channel.UniqueId != "X") return;
            channels.GetByUniqueId("X").Should().BeNull();
            channel.SyncRoot.IsHeldByCurrentThread.Should().BeFalse();
            Task.Run(() => rig.Add("probe", "probe")).WaitAsync(EndingBaselineRig.Bound).GetAwaiter().GetResult();
            Interlocked.Increment(ref callbackCount);
        };
        if (snapshot)
        {
            using var window = channels.OpenReadWindow();
            channels.ReconcileWithSnapshot([], window);
        }
        else rig.Hangup("X");
        callbackCount.Should().Be(1);
        output.WriteLine($"snapshot={snapshot}; removal visible, channel lock free, independent manager mutation completed");
    }

    [Fact]
    public async Task OnHangup_ShouldRemoveBeforePostingAndNotifyOnlyAfterUnlock_WhenTheChannelLockIsHeld()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach(); rig.Add("X");
        var channels = rig.Run.Server.Channels;
        var channel = channels.GetByUniqueId("X")!;
        var call = rig.Manager.GetByLinkedId("L")!;
        var callbacks = 0;
        channels.ChannelRemoved += c => { if (c.UniqueId == "X") Interlocked.Increment(ref callbacks); };
        Task worker;
        bool removed;
        int before;
        int engineBefore;
        lock (channel.SyncRoot)
        {
            worker = Task.Run(() => rig.Hangup("X"));
            // fence-allow: GUARD-TIMEOUT — observe actual table removal before unlocking the channel and permitting callbacks.
            removed = SpinWait.SpinUntil(() => channels.GetByUniqueId("X") is null, EndingBaselineRig.Bound);
            before = Volatile.Read(ref callbacks);
            engineBefore = rig.Ends(call);
        }
        await worker.WaitAsync(EndingBaselineRig.Bound);
        new { Removed = removed, Before = before, EngineBefore = engineBefore, After = callbacks, EngineAfter = rig.Ends(call) }
            .Should().BeEquivalentTo(new { Removed = true, Before = 0, EngineBefore = 0, After = 1, EngineAfter = 1 });
        output.WriteLine("table removal observed while X.SyncRoot held; 0 callbacks/engine endings before unlock, 1 each after worker completion");
    }
}

public sealed class NotificationGateCleanupTests
{
    [Fact]
    public async Task RunAsync_ShouldReleaseAndJoinEveryWorker_WhenTheTestBodyThrows()
    {
        using var gate = new NotificationGate();
        var workers = new List<Task> { Task.Run(gate.Hold) };
        var error = await Record.ExceptionAsync(() => NotificationGate.RunAsync([gate], workers, async () =>
        {
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            throw new InvalidOperationException("intentional cleanup negative control");
        }));
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("intentional cleanup negative control");
        workers.Should().OnlyContain(t => t.IsCompletedSuccessfully);
    }
}

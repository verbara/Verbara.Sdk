using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A channel snapshot is reconciled behind the AMI connection's dispatch. The connection's reader hands a list action's
/// events, <c>StatusComplete</c> included, straight to the action's collector, while every other event (a
/// <c>Hangup</c> among them) waits in its pump; so a <c>Hangup</c> queued behind a slow handler when
/// <c>StatusComplete</c> arrives must still be applied before the snapshot ends anything, and the call ends with the
/// <c>Hangup</c>'s cause rather than with <c>cause = reload</c>. A handler that never returns bounds that wait: once the
/// bound runs out the reconciliation ends nothing, and a load that ran out schedules one full reload. A reconciliation
/// awaited from inside a handler cannot wait for the dispatch it blocks, so it runs without that wait.
/// </summary>
/// <remarks>
/// <para>
/// In process and deterministic: a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>,
/// whose dispatch a <see cref="DispatchGate"/> holds, on the run's manual clock. The peer writes the gate's event and
/// then the <c>Hangup</c>, so the <c>Hangup</c> is queued behind the held handler before <c>Status</c> is answered.
/// </para>
/// <para>
/// Unfixed code does not wait for the pump, so its reconciliation ends before the test releases the gate. Fixed code
/// waits, and its wait arms the bound as a timer on the server's clock (an internal constant adjustable
/// through the server's <see cref="TimeProvider"/>). Each test therefore waits for whichever comes first — the
/// reconciliation's end, or that timer — and only then releases the gate, or advances the clock by the timer's due time
/// where the case is the bound running out.
/// </para>
/// </remarks>
public sealed class ReloadBehindHeldDispatchTests
{
    private const string ServerId = "srv-1";
    private const string StateLoaded = "[LIVE] State loaded";
    private const string ChannelsReconciled = "[LIVE] Channels reconciled";
    private const string CallUid = "1790633830.0";
    private const string CallChannel = "PJSIP/pstn-00000000";
    private const string HandlerTrigger = "reconcile-from-handler";

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldEndTheCallWithTheHangupsCause_WhenTheHangupWaitsInThePumpAsStatusCompletes()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        using var gate = new DispatchGate();
        using var gateSubscription = run.Connection.Subscribe(gate);
        await using var manager = NewManager();
        var ended = new ConcurrentQueue<CallEndedEvent>();
        using var endings = manager.Events.Subscribe(new OnNext(e =>
        {
            if (e is CallEndedEvent ce)
                ended.Enqueue(ce);
        }));
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var tail = new TailObserver();
        using var tailSubscription = run.Connection.Subscribe(tail);
        await OpenCallAsync(peer, run);

        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(CallUid, CallChannel, CallUid, cause: 17, causeText: "User busy"));
        await gate.Held.WaitAsync(Run.Bound);

        peer.StatusChannels = [];
        var reconcile = run.Server.ReconcileChannelsAsync().AsTask();
        await EndsOrWaitsOnItsBarrierAsync(run, reconcile);
        gate.Release();
        await reconcile.WaitAsync(Run.Bound);
        await tail.Hangup.WaitAsync(Run.Bound);

        var session = manager.GetByLinkedId(CallUid);
        new
        {
            SessionHangupCause = session?.HangupCause,
            CauseMetadata = session?.Metadata.GetValueOrDefault("cause"),
            Endings = ended.Select(e => e.Cause).ToArray(),
            GateTimedOut = gate.TimedOut,
        }.Should().BeEquivalentTo(
            new
            {
                SessionHangupCause = (HangupCause?)HangupCause.UserBusy,
                CauseMetadata = default(string),
                Endings = new HangupCause?[] { HangupCause.UserBusy },
                GateTimedOut = false,
            },
            "the Hangup was received before StatusComplete and waited in the pump behind a held handler; it is applied "
            + "before the snapshot ends anything, so the call ends once, with the Hangup's cause and no 'cause' metadata. "
            + $"Measured: cause={session?.Metadata.GetValueOrDefault("cause") ?? "(none)"}");
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldEndNothingAndWarn_WhenAWedgedHandlerOutlastsTheBound()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        using var gate = new DispatchGate();
        using var gateSubscription = run.Connection.Subscribe(gate);
        await using var manager = NewManager();
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        await OpenCallAsync(peer, run);

        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        var warningsBefore = Warnings(run.ServerLog);

        peer.StatusChannels = [];
        var reconcile = run.Server.ReconcileChannelsAsync().AsTask();
        await RunOutEveryBarrierAsync(run, reconcile);
        var warnings = Warnings(run.ServerLog) - warningsBefore;
        gate.Release();

        var session = manager.GetByLinkedId(CallUid);
        new
        {
            ChannelHeld = run.Server.Channels.GetByUniqueId(CallUid) is not null,
            SessionEnded = session?.State is CallSessionState.Completed or CallSessionState.Failed,
            CauseMetadata = session?.Metadata.GetValueOrDefault("cause"),
            WarningLogged = warnings >= 1,
        }.Should().BeEquivalentTo(
            new
            {
                ChannelHeld = true,
                SessionEnded = false,
                CauseMetadata = default(string),
                WarningLogged = true,
            },
            "a handler that never returns holds every event behind it; once the bound runs out the reconciliation ends "
            + "nothing it could not prove, and logs a Warning. Measured: "
            + string.Join(" | ", run.ServerLog.Entries.Select(e => $"{e.Level}: {e.Line}")));
    }

    [Fact]
    public async Task StartAsync_ShouldAdmitTheListedChannelsEndNothingAndScheduleOneFullReload_WhenAWedgedHandlerOutlastsTheBound()
    {
        const string listedUid = "1790633831.0";
        const string listedChannel = "PJSIP/pstn-00000001";
        var statusAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusAnsweredAfter = statusAnswer.Task,
            StatusChannels = [new StatusChannel(listedUid, listedChannel, LinkedId: listedUid)],
        };
        await using var run = await Run.ConnectAsync(peer);
        using var gate = new DispatchGate();
        using var gateSubscription = run.Connection.Subscribe(gate);
        var removed = new ConcurrentQueue<(string UniqueId, bool ByReload, HangupCause Cause)>();
        run.Server.Channels.ChannelRemoved += c => removed.Enqueue((c.UniqueId, c.RemovedByReload, c.HangupCause));
        var start = run.Server.StartAsync();
        // Subscribed after the server, which subscribes before the start's first await.
        var tail = new TailObserver();
        using var tailSubscription = run.Connection.Subscribe(tail);
        await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        statusAnswer.TrySetResult();
        await EndsOrWaitsOnItsBarrierAsync(run, start);

        // A Hangup Asterisk sends after its Status answer, for a channel that answer listed: queued behind the handler.
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(listedUid, listedChannel, listedUid));
        await RunOutEveryBarrierAsync(run, start);
        var admittedByTheLoad = run.Server.Channels.GetByUniqueId(listedUid) is not null;
        var removedDuringTheLoad = removed.Count;
        gate.Release();
        await tail.Hangup.WaitAsync(Run.Bound);
        var reloaded = await CompletesWithinBoundAsync(run.ServerLog.Logged(StateLoaded, 2));

        new
        {
            AdmittedByTheLoad = admittedByTheLoad,
            RemovedDuringTheLoad = removedDuringTheLoad,
            RemovedAfter = removed.ToArray(),
            FullReloadScheduled = reloaded,
            StatusAsks = peer.Asked("Status"),
        }.Should().BeEquivalentTo(
            new
            {
                AdmittedByTheLoad = true,
                RemovedDuringTheLoad = 0,
                RemovedAfter = new[] { (listedUid, false, HangupCause.NormalClearing) },
                FullReloadScheduled = true,
                StatusAsks = 2,
            },
            "a first load whose barrier ran out applies only its non-destructive half: it admits what Status listed and "
            + "ends nothing; it schedules one full reload, since the sweep never runs over an empty table; and the Hangup "
            + "that waited behind the handler then removes the admitted channel with its cause. Server log: "
            + string.Join(" | ", run.ServerLog.Entries.Select(e => $"{e.Level}: {e.Line}")));
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldCompleteWithoutTheBarrierAndLogAtDebug_WhenAwaitedFromInsideAHandler()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        var log = new SignalingLogger<VerbaraServer> { MinimumLevel = LogLevel.Debug };
        await using var server = new VerbaraServer(run.Connection, log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        var debugBefore = log.Entries.Count;
        var fromHandler = new FromHandler(server);
        using var handlerSubscription = run.Connection.Subscribe(fromHandler);

        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", HandlerTrigger)]);
        var completed = await fromHandler.Returned.WaitAsync(Run.Bound);

        var debugLines = log.Entries.Skip(debugBefore)
            .Where(e => e.Level == LogLevel.Debug && e.Line.StartsWith("[LIVE]", StringComparison.Ordinal)
                && !e.Line.StartsWith(ChannelsReconciled, StringComparison.Ordinal))
            .Select(e => e.Line).ToArray();
        new { Completed = completed, Fault = fromHandler.Fault, DebugLineAboutTheBarrier = debugLines.Length >= 1 }
            .Should().BeEquivalentTo(
                new { Completed = true, Fault = default(string), DebugLineAboutTheBarrier = true },
                "called from inside a handler, the reconciliation cannot wait for the dispatch that handler blocks: it runs "
                + "without the barrier, completes, and says so in a [LIVE] line at Debug, beside its usual result line and the "
                + "channel table's own [CHANNEL] line. Lines after the "
                + "call: " + string.Join(" | ", log.Entries.Skip(debugBefore).Select(e => $"{e.Level}: {e.Line}")));
    }

    /// <summary>Opens one call: the peer's <c>Newchannel</c>, waited for until the server holds the channel.</summary>
    private static async Task OpenCallAsync(BootingAsterisk peer, Run run)
    {
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelAdded += c =>
        {
            if (c.UniqueId == CallUid)
                added.TrySetResult();
        };
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(CallUid, CallChannel, CallUid));
        await added.Task.WaitAsync(Run.Bound);
    }

    /// <summary>
    /// Waits until <paramref name="work"/> has ended (unfixed code: nothing makes it wait for the pump) or has begun to
    /// wait on its barrier, which arms the barrier's bound as a timer on the run's clock.
    /// </summary>
    private static async Task EndsOrWaitsOnItsBarrierAsync(Run run, Task work)
    {
        using var stop = new CancellationTokenSource();
        var timer = run.Clock.TimersCreated.ReadAsync(stop.Token).AsTask();
        await Task.WhenAny(work, timer).WaitAsync(Run.Bound);
        await stop.CancelAsync();
    }

    /// <summary>
    /// Runs <paramref name="work"/> to its end, advancing the run's clock past each timer it arms on the way — each
    /// barrier's bound running out — so the test reaches the end without the held handler returning.
    /// </summary>
    private static async Task RunOutEveryBarrierAsync(Run run, Task work)
    {
        while (!work.IsCompleted)
        {
            using var stop = new CancellationTokenSource();
            var timer = run.Clock.TimersCreated.ReadAsync(stop.Token).AsTask();
            var first = await Task.WhenAny(work, timer).WaitAsync(Run.Bound);
            await stop.CancelAsync();
            if (first == timer && timer.IsCompletedSuccessfully)
                run.Clock.Advance(timer.Result.DueTime);
        }

        await work;
    }

    private static int Warnings(SignalingLogger<VerbaraServer> log) => log.Entries.Count(e => e.Level == LogLevel.Warning);

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static CallSessionManager NewManager() =>
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore());

    /// <summary>Subscribed after the server: once it has seen a <c>Hangup</c>, the server's observer has handled it.</summary>
    private sealed class TailObserver : IObserver<ManagerEvent>
    {
        private readonly TaskCompletionSource _hangup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Hangup => _hangup.Task;

        public void OnNext(ManagerEvent value)
        {
            if (value is HangupEvent)
                _hangup.TrySetResult();
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

    /// <summary>A handler that awaits the server's channel reconciliation, synchronously, on the trigger event.</summary>
    private sealed class FromHandler(VerbaraServer server) : IObserver<ManagerEvent>
    {
        private readonly TaskCompletionSource<bool> _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the handler's call returned: whether the reconciliation completed within the bound.</summary>
        public Task<bool> Returned => _returned.Task;

        public string? Fault { get; private set; }

        public void OnNext(ManagerEvent value)
        {
            if (value is not UserEventEvent { UserEvent: HandlerTrigger })
                return;

            try
            {
                _returned.TrySetResult(server.ReconcileChannelsAsync().AsTask().Wait(Run.Bound));
            }
            catch (AggregateException ex)
            {
                Fault = ex.InnerException?.Message ?? ex.Message;
                _returned.TrySetResult(false);
            }
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

    /// <summary>A session subscriber that runs <c>onNext</c> on each notification.</summary>
    private sealed class OnNext(Action<SessionDomainEvent> onNext) : IObserver<SessionDomainEvent>
    {
        void IObserver<SessionDomainEvent>.OnNext(SessionDomainEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to do.
        }
    }

    /// <summary>A store that saves nothing; the tests read the manager and Live's tables.</summary>
    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}

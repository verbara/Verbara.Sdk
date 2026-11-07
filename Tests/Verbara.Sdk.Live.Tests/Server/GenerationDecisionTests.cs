using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class GenerationDecisionTests
{
    [Fact]
    public async Task OnChannelRemoved_ShouldEndAnObservedCall_WhenItsTableIsNotComplete()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var incomplete = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        incomplete.Server.ChannelTableComplete.Should().BeFalse();
        rig.Manager.AttachToServer(incomplete.Server, "server");
        incomplete.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("server", "L")!;

        incomplete.Server.Channels.OnHangup("X", HangupCause.NormalClearing);

        rig.Ends(call).Should().Be(1, "an ordinary live call is born stamped with its delivering attachment");
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }

    [Fact]
    public async Task ChannelSnapshotCompleted_ShouldDecideALeftCall_WhenAnOlderDecisionOvertookTheReattach()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Manager.DetachFromServer("server");
        using var gate = new NotificationGate();
        var once = 0;
        rig.Manager.StepHook = (step, _, _) =>
        {
            if (step == HandlerStep.DecisionOracleCaptured && Interlocked.Exchange(ref once, 1) == 0)
                gate.Hold();
        };
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(rig.Attach));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.DetachFromServer("server");
            rig.Manager.AttachToServer(replacement.Server, "server");
            gate.Release();
            await workers[0].WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.StepHook = null;
            replacement.Server.ChannelTableComplete.Should().BeFalse();
            await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        });

        rig.Ends(call).Should().Be(1, "the old decision must stamp its own oracle, leaving the new attachment pending");
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
        call.Participants.Should().OnlyContain(p => p.LeftAt.HasValue);
    }

    [Fact]
    public async Task ChannelSnapshotCompleted_ShouldDecideACall_WhenAReplacedAttachmentDeliveredItsArrival()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        var once = 0;
        rig.Run.Server.Channels.ChannelAdded += _ =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            rig.Manager.DetachFromServer("server");
            rig.Manager.AttachToServer(replacement.Server, "server");
        };
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        replacement.Server.ChannelTableComplete.Should().BeFalse();

        await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);

        rig.Ends(call).Should().Be(1, "the captured old delivery must not borrow the replacement's birth stamp");
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
    }

    [Fact]
    public async Task AttachToServer_ShouldReturnWithinItsBound_WhenASubscriberHoldsALeftCallsLock()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await EndingBaselineRig.CreateAsync();
        var log = new DecisionLog();
        var endings = 0;
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()), log, new Store());
        manager.AttachToServer(rig.Run.Server, "server");
        rig.Add("X");
        var call = manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        using var subscription = manager.Events.Subscribe(new Observer(e =>
        {
            if (e is CallHeldEvent) gate.Hold();
            if (e is CallEndedEvent ended && ended.SessionId == call.SessionId) Interlocked.Increment(ref endings);
        }));
        var workers = new List<Task>();
        var returnedWhileHeld = false;
        var errorsWhileHeld = 0;
        var pendingAfterRelease = false;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Run.Server.Channels.OnHold("X", null)));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            manager.DetachFromServer("server");
            var attach = Task.Run(() => manager.AttachToServer(replacement.Run.Server, "server"));
            workers.Add(attach);
            // fence-allow: GUARD-TIMEOUT — detects the missing one-second acquisition bound while the subscriber holds the lock.
            returnedWhileHeld = await Task.WhenAny(attach, Task.Delay(TimeSpan.FromSeconds(3))) == attach;
            errorsWhileHeld = log.Entries.Count(e => e.Level == LogLevel.Error
                && e.Message.Contains("server", StringComparison.Ordinal)
                && e.Message.Contains(call.SessionId, StringComparison.Ordinal));
            if (returnedWhileHeld)
            {
                call.State.Should().Be(CallSessionState.OnHold);
                // The installed subscriptions must work even when the deciding walk skipped this call.
                replacement.Add("subscription", "other");
                manager.GetByChannelId("server", "subscription").Should().NotBeNull();
            }
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
            pendingAfterRelease = manager.IsPendingDecision(call);
        });

        returnedWhileHeld.Should().BeTrue("attach must stop waiting before the subscriber gives up its call lock");
        errorsWhileHeld.Should().Be(1, "one Error names the skipped server and session");
        pendingAfterRelease.Should().BeTrue("the skipped call must retain its delayed generation");
        await replacement.Run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        call.State.Should().Be(CallSessionState.Completed);
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
        Volatile.Read(ref endings).Should().Be(1, "the actual ending notification must be delivered exactly once");
    }

    [Fact]
    public async Task OnChannelRemoved_ShouldKeepTheLeftCall_WhenItsIncompleteAttachmentIsBeingPublished()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Manager.DetachFromServer("server");
        replacement.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        var delivered = false;
        rig.Manager.StepHook = (step, _, _) =>
        {
            if (step != HandlerStep.AttachmentPublicationStarted) return;
            replacement.Server.Channels.OnHangup("X", HangupCause.NormalClearing);
            delivered = true;
        };

        rig.Manager.AttachToServer(replacement.Server, "server");
        rig.Manager.StepHook = null;

        delivered.Should().BeTrue();
        replacement.Server.ChannelTableComplete.Should().BeFalse();
        rig.Ends(call).Should().Be(0, "a newly publishing attachment is not a closed, unreplaced attachment");
        await replacement.Server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachToServer_ShouldDecideTheNewCall_WhenItsArrivalPrecededItsDepartureSubscription(bool complete)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var incomplete = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        var server = complete ? rig.Run.Server : incomplete.Server;
        CallSession? call = null;
        rig.Manager.StepHook = (step, _, _) =>
        {
            if (step != HandlerStep.AttachmentArrivalSubscribed) return;
            server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
            call = rig.Manager.GetByLinkedId("server", "L");
            server.Channels.OnHangup("X", HangupCause.NormalClearing);
        };

        rig.Manager.AttachToServer(server, "server");
        rig.Manager.StepHook = null;

        call.Should().NotBeNull("the arrival subscription was installed before the captured interleaving");
        if (!complete)
        {
            rig.Ends(call!).Should().Be(0, "an incomplete table must wait for its first completed snapshot");
            await server.ReconcileChannelsAsync().AsTask().WaitAsync(EndingBaselineRig.Bound);
        }
        rig.Ends(call!).Should().Be(1, "a call born before installation must remain eligible for iii or completion");
        call!.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull("the departure occurred before the engine subscribed it");
    }

    [Fact]
    public async Task AttachToServer_ShouldSkipRetainedTerminalCalls_WhenAnEndingSubscriberHoldsTheirLock()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await EndingBaselineRig.CreateAsync();
        var log = new DecisionLog();
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()), log, new Store());
        manager.AttachToServer(rig.Run.Server, "server");
        rig.Add("X");
        var call = manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        var endings = 0;
        using var subscription = manager.Events.Subscribe(new Observer(e =>
        {
            if (e is not CallEndedEvent ended || ended.SessionId != call.SessionId) return;
            Interlocked.Increment(ref endings);
            gate.Hold();
        }));
        var workers = new List<Task>();
        var errorsWhileHeld = -1;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            manager.GetById(call.SessionId).Should().BeSameAs(call, "the ended call is retained while its subscriber runs");
            manager.DetachFromServer("server");
            var attach = Task.Run(() => manager.AttachToServer(replacement.Run.Server, "server"));
            workers.Add(attach);
            await attach.WaitAsync(EndingBaselineRig.Bound);
            errorsWhileHeld = log.Entries.Count(e => e.Level == LogLevel.Error
                && e.Message.Contains(call.SessionId, StringComparison.Ordinal));
            replacement.Add("subscription", "other");
            manager.GetByChannelId("server", "subscription").Should().NotBeNull();
            gate.Release();
        });

        errorsWhileHeld.Should().Be(0, "a terminal retained call is never a pending attach decision");
        Volatile.Read(ref endings).Should().Be(1);
        manager.GetById(call.SessionId).Should().BeSameAs(call);
        call.State.Should().Be(CallSessionState.Completed);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }

    private sealed class Observer(Action<SessionDomainEvent> next) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
    private sealed class DecisionLog : ILogger<CallSessionManager>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((level, formatter(state, exception)));
    }
    private sealed class Store : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}

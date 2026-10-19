using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Live.Tests.Queues;

[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotBatchBoundaryTests
{
    [Fact]
    public async Task StartAsync_ShouldDiscardARejectedAttempt_WhenQueueStatusRetriesWithAnEmptyCompletedAnswer()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true, SendsFullyBooted = false,
            QueueEntries = [new QueueCaller("PJSIP/partial", "partial-1", 1, 3)],
            QueueStatusRefusalAfterRows = "Invalid/unknown command: QueueStatus.",
        };
        await using var run = await Run.ConnectAsync(peer, barrierClock: new FakeTimeProvider());
        var joins = 0;
        var members = 0;
        run.Server.Queues.CallerJoined += (_, _) => joins++;
        run.Server.Queues.MemberAdded += (_, _) => members++;
        var load = run.StartServerAsync();
        var retry = await run.Clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
        var queuesBeforeRetry = run.Server.Queues.QueueCount;
        peer.QueueParamsIncluded = false;
        peer.QueueMemberIncluded = false;
        peer.QueueEntries = [];
        peer.QueueStatusRefusalAfterRows = null;
        run.Clock.Advance(retry.DueTime);
        await load;

        using var scope = new AssertionScope();
        queuesBeforeRetry.Should().Be(0, "the first attempt was refused even though it carried rows");
        run.Server.Queues.QueueCount.Should().Be(0, "the accepted second attempt was empty, without rejected rows");
        joins.Should().Be(0);
        members.Should().Be(0);
        peer.Asked("QueueStatus").Should().Be(2);
        run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
        peer.Fault.Should().BeNull();
    }
    [Fact]
    public async Task StartAsync_ShouldObserveBufferedRowsWithoutPublishingPartialState_WhenTheBufferObserverReturns()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        var rows = 0;
        run.Server.QueueRowBuffered = () =>
        {
            rows++;
            run.Server.Queues.QueueCount.Should().Be(0, "collection has no public table transitions");
        };
        await run.StartServerAsync();
        rows.Should().BeGreaterThanOrEqualTo(2);
        run.Server.Queues.QueueCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldPropagateTheQueueRowObservationFailure_WhenTheBufferObserverThrows()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        run.Server.QueueRowBuffered = () => throw new InvalidOperationException("queue-row-buffer-hook-sentinel");

        var failure = await Record.ExceptionAsync(() => run.StartServerAsync());

        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("queue-row-buffer-hook-sentinel");
        run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
        run.Server.Queues.QueueCount.Should().Be(0);
        run.Server.IsLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task DeferredQueueReports_ShouldDiscardAReachedOldFence_WhenAnotherSessionIsAlreadyConnected()
    {
        var queueAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            QueueEntries = [new QueueCaller("PJSIP/old-session", "old-session", 1, 3)],
            AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = queueAnswer.Task },
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        var barrierClock = new FakeTimeProvider();
        await using var run = await Run.ConnectAsync(first, second, barrierClock: barrierClock);
        using var gate = new DispatchGate();
        using var held = run.Connection.Subscribe(gate);
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = run.Connection.Subscribe(new EventObserver(e =>
        {
            if (e is UserEventEvent { UserEvent: "old-fence-reached" })
                tail.TrySetResult();
        }));
        var reports = 0;
        run.Server.Queues.CallerJoined += (_, _) => reports++;
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list != "deferred")
                return;
            ownerEntered.TrySetResult();
            releaseOwner.Task.WaitAsync(Run.Bound).GetAwaiter().GetResult();
        };
        try
        {
            var load = run.Server.StartAsync();
            await first.AskedAtLeast("QueueStatus", 1).WaitAsync(Run.Bound);
            while (barrierClock.TimersCreated.TryRead(out _)) { }
            await first.SendEventAsync("UserEvent", DispatchGate.Frame());
            await gate.Held.WaitAsync(Run.Bound);
            queueAnswer.TrySetResult();
            var timer = await barrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
            barrierClock.Advance(timer.DueTime);
            await load.WaitAsync(Run.Bound);
            await ownerEntered.Task.WaitAsync(Run.Bound);
            gate.Release();
            await first.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "old-fence-reached")]);
            await tail.Task.WaitAsync(Run.Bound);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Connection.StateChanged += change =>
            {
                if (change.Current == AmiConnectionState.Disconnected)
                    disconnected.TrySetResult();
            };
            first.CloseSession();
            await disconnected.Task.WaitAsync(Run.Bound);
            var connect = run.Connection.ConnectAsync().AsTask();
            run.ReleaseSecondPeer();
            await connect.WaitAsync(Run.Bound);
            releaseOwner.TrySetResult();
            await run.Server.ScheduledReload.WaitAsync(Run.Bound);

            using var scope = new AssertionScope();
            run.Connection.State.Should().Be(AmiConnectionState.Connected);
            reports.Should().Be(0, "a reached old fence must not publish an old-session report into the new session");
            second.Asked("Status").Should().Be(0, "the old owner must not reload the new session");
            run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0);
            run.ServerLog.Entries.Should().NotContain(e => e.Line.Contains("[LIVE] The full reload scheduled after", StringComparison.Ordinal),
                "cancellation follows the session identity, not a hidden observer/worker failure");
        }
        finally
        {
            queueAnswer.TrySetResult();
            releaseOwner.TrySetResult();
            gate.Release();
        }
    }

    private sealed class EventObserver(Action<ManagerEvent> next) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

}

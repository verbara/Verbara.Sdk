using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Channels;

public sealed class ChannelManagerVisitHeldTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisitHeld_ShouldTakeChannelBeforeCallAndReleaseManagerGate_WhenTheCallbackTakesACallLock(bool restricted)
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var call = new CallSession("session", "call", "server", CallDirection.Inbound);
        var visits = 0;
        Task? lookup = null;
        Visit(manager, restricted, channel =>
        {
            channel.SyncRoot.IsHeldByCurrentThread.Should().BeTrue("the visitor owns the channel before taking the call lock");
            call.SyncRoot.IsHeldByCurrentThread.Should().BeFalse();
            lock (call.SyncRoot)
            {
                channel.SyncRoot.IsHeldByCurrentThread.Should().BeTrue();
                call.SyncRoot.IsHeldByCurrentThread.Should().BeTrue();
                // Opening a read window actually takes the manager gate; a lock-free lookup would not test it.
                lookup = Task.Run(() =>
                {
                    using var window = manager.OpenReadWindow();
                    manager.GetByUniqueId("x").Should().BeSameAs(channel);
                });
                lookup.Wait(Bound).Should().BeTrue("no manager gate may span the callback or call lock");
                visits++;
            }
        });
        if (lookup is not null) await lookup.WaitAsync(Bound);
        visits.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("call")]
    public void VisitHeld_ShouldRestrictToTheEngineKey_WhenOtherLinkedIdsAreHeld(string? linkedId)
    {
        var manager = NewManager();
        Add(manager, "x", linkedId);
        Add(manager, "other", "elsewhere");
        Add(manager, "case", "CALL");
        var key = string.IsNullOrEmpty(linkedId) ? "x" : linkedId;
        var seen = new List<string>();
        manager.VisitHeld(key, channel => seen.Add(channel.UniqueId));
        seen.Should().Equal("x");
        manager.VisitHeld("absent", _ => throw new InvalidOperationException("unrelated channel visited"));
        seen.Clear();
        manager.VisitHeld(channel => seen.Add(channel.UniqueId));
        seen.Should().BeEquivalentTo(["x", "other", "case"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisitHeld_ShouldNotHoldManagerGateWhileWaitingForChannel_WhenAnotherThreadOwnsSyncRoot(bool restricted)
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var channel = manager.GetByUniqueId("x")!;
        var visits = 0;
        (Thread Thread, Task Done)? worker = null;
        Task? lookup = null;
        using var gateRead = new ManualResetEventSlim();
        try
        {
            lock (channel.SyncRoot)
            {
                worker = StartThread(() => Visit(manager, restricted, _ => Interlocked.Increment(ref visits)));
                WaitUntilBlocked(worker.Value.Thread);
                lookup = Task.Run(() =>
                {
                    using var window = manager.OpenReadWindow();
                    gateRead.Set();
                });
                gateRead.Wait(Bound).Should().BeTrue("a visitor waiting for a channel must not own the manager gate");
                visits.Should().Be(0);
            }
        }
        finally
        {
            if (lookup is not null) await lookup.WaitAsync(Bound);
            if (worker is not null) await worker.Value.Done.WaitAsync(Bound);
        }
        visits.Should().Be(1);
    }

    [Fact]
    public async Task VisitHeld_ShouldSkipTheCapturedInstance_WhenItsUidIsReplacedBeforeTheChannelLockIsAcquired()
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var old = manager.GetByUniqueId("x")!;
        var seen = new List<AsteriskChannel>();
        (Thread Thread, Task Done) visitor;
        Task removal;
        lock (old.SyncRoot)
        {
            visitor = StartThread(() => manager.VisitHeld("call", seen.Add));
            WaitUntilBlocked(visitor.Thread); // The indexed group has been captured; the old lock is next.
            removal = Task.Run(() => manager.OnHangup("x"));
            SpinWait.SpinUntil(() => manager.GetByUniqueId("x") is null, Bound).Should().BeTrue();
            Add(manager, "x", "call");
            manager.GetByUniqueId("x").Should().NotBeSameAs(old);
        }
        await Task.WhenAll(visitor.Done, removal).WaitAsync(Bound);
        seen.Should().BeEmpty("the identity check must run after taking the old instance's lock");
        manager.VisitHeld("call", seen.Add);
        seen.Should().ContainSingle().Which.Should().BeSameAs(manager.GetByUniqueId("x"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VisitHeld_ShouldFinishBeforeRemovalIsPostedAndDrained_WhenRemovalLeavesTheTableDuringTheVisit(bool restricted, bool reload)
    {
        var manager = NewManager();
        Add(manager, "x", "call");
        var channel = manager.GetByUniqueId("x")!;
        var call = new CallSession("session", "call", "server", CallDirection.Inbound);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var visited = false;
        var removals = 0;
        var departuresVisited = 0;
        manager.ChannelRemoved += removed =>
        {
            removed.Should().BeSameAs(channel);
            removed.SyncRoot.IsHeldByCurrentThread.Should().BeFalse("the outbox raises after the posting channel lock is released");
            lock (call.SyncRoot)
            {
                visited.Should().BeTrue("the visit's call mutation precedes the removal notification");
                Interlocked.Increment(ref removals);
            }
        };
        var visit = Task.Run(() => Visit(manager, restricted, current =>
        {
            current.SyncRoot.IsHeldByCurrentThread.Should().BeTrue();
            lock (call.SyncRoot)
            {
                entered.Set();
                release.Wait(Bound).Should().BeTrue();
                current.State.Should().Be(ChannelState.Up, "removal cannot mutate or post until it takes this channel lock");
                visited = true;
            }
        }));
        Task? removal = null;
        try
        {
            entered.Wait(Bound).Should().BeTrue();
            removal = Task.Run(() =>
            {
                if (!reload) manager.OnHangup("x");
                else
                {
                    using var window = manager.OpenReadWindow();
                    manager.ReconcileWithSnapshot([], window);
                }
            });
            SpinWait.SpinUntil(() => manager.GetByUniqueId("x") is null, Bound).Should().BeTrue();
            ChannelManager.IsDeparting(channel).Should().BeTrue();
            Volatile.Read(ref removals).Should().Be(0);
            removal.IsCompleted.Should().BeFalse();
            // The unrestricted walk has no table entry; the restricted index still lists the departure.
            var departingVisit = Task.Run(() => Visit(manager, restricted, _ => Interlocked.Increment(ref departuresVisited)));
            release.Set();
            await Task.WhenAll(visit, removal, departingVisit).WaitAsync(Bound);
        }
        finally
        {
            release.Set();
            await visit.WaitAsync(Bound);
            if (removal is not null) await removal.WaitAsync(Bound);
        }
        removals.Should().Be(1);
        departuresVisited.Should().Be(0, "a departing instance is never handed to the callback");
        manager.ChannelsOfLinkedId("call").Length.Should().Be(0);
    }

    private static void Visit(ChannelManager manager, bool restricted, Action<AsteriskChannel> visit)
    {
        if (restricted) manager.VisitHeld("call", visit);
        else manager.VisitHeld(visit);
    }

    private static (Thread Thread, Task Done) StartThread(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, done.Task);
    }

    private static void WaitUntilBlocked(Thread thread) =>
        SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != ThreadState.Running, Bound)
            .Should().BeTrue("the dedicated visitor must actually be waiting for the held channel lock");

    private static ChannelManager NewManager() => new(NullLogger.Instance);
    private static void Add(ChannelManager manager, string id, string? linkedId) =>
        manager.OnNewChannel(id, "PJSIP/" + id, ChannelState.Up, linkedId: linkedId);
}

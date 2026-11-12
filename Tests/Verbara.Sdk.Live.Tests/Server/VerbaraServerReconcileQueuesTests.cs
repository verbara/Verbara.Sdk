using Verbara.Sdk.Ami;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ReconcileQueuesAsync"/>: the queue part of a load and nothing else. Asterisk announces no
/// queue it creates after Live's load, by a reload of its static configuration or a realtime insert; the explicit
/// refresh finds it, with its members and waiting callers, without reconnecting and without asking for the channels,
/// the bridges, the conference rooms or the agents. It reconciles under the load's rules: a held caller it still lists
/// keeps its entry, its join instant and its one counted join; a refused, cancelled or cut-short read removes nothing.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that has finished starting. On the AMI wire a queue created by
/// a static reload and one inserted in realtime answer <c>QueueStatus</c> the same way, so the peer plays both as a queue
/// listed after the load; which of them Asterisk does list is measured on the real substrate (task 3.0). In the counter
/// collection, because the tests count joins and leaves exactly.
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class VerbaraServerReconcileQueuesTests
{
    private const string Sales = "sales";
    private const string Support = "support";
    private static readonly QueueCaller Waiting = new("PJSIP/far-00000011", "1790633900.11", Position: 1, Wait: 4);
    private static readonly QueueCaller HeldCaller = new("PJSIP/far-00000001", "1790633900.1", Position: 1, Wait: 2);

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldHoldTheNewQueuesTheirMembersAndCallers_WhenTheyWereCreatedAfterTheLoadWithoutAnEvent()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var asked = Asks(peer);

        // A static queue added by a reload, and a realtime one: Asterisk sends no event for either.
        peer.OtherQueues =
        [
            new ListedQueue(Sales, ["PJSIP/2001", "PJSIP/2002"], [Waiting]),
            new ListedQueue(Support, ["PJSIP/3001"], [], Strategy: "leastrecent"),
        ];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        var sales = run.Server.Queues.GetByName(Sales);
        var support = run.Server.Queues.GetByName(Support);
        using (new AssertionScope())
        {
            sales.Should().NotBeNull("the refresh holds the queue Asterisk lists and Live did not");
            sales?.Strategy.Should().Be("rrmemory", "with the configuration the listing reports");
            sales?.Members.Keys.Should().BeEquivalentTo(["PJSIP/2001", "PJSIP/2002"], "and its listed members");
            sales?.Entries.Keys.Should().Equal([Waiting.Channel], "and the caller waiting in it");
            support.Should().NotBeNull("the second created queue is held too");
            support?.Strategy.Should().Be("leastrecent");
            support?.Members.Keys.Should().Equal(["PJSIP/3001"]);
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().NotBeNull("the queue of the load is kept");
            Asks(peer).Should().Be(asked with { QueueStatus = asked.QueueStatus + 1 },
                "the refresh asks QueueStatus once and nothing else: no Status, BridgeList, ConfbridgeListRooms or Agents");
            peer.QueueStatusFilters.Should().HaveCount(2).And.OnlyContain(f => f == null,
                "the refresh asks every queue, as the load does");
            run.Connection.State.Should().Be(AmiConnectionState.Connected, "nothing reconnected");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldKeepTheSameEntryItsJoinInstantAndItsOneCountedJoin_WhenTheRefreshStillListsTheCaller()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        var held = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        var joinedAt = held.JoinedAt;
        using var counters = new LiveCounters();

        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        var after = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        using (new AssertionScope())
        {
            after.Should().BeSameAs(held, "a caller the refresh still lists keeps the entry Live holds");
            after.JoinedAt.Should().Be(joinedAt, "its join instant is kept");
            after.FromSnapshot.Should().BeTrue("its provenance is the load's snapshot, unchanged");
            after.ReportedWaitSeconds.Should().Be(HeldCaller.Wait, "and so is the wait Asterisk reported then");
            counters.Joined.Should().Be(0, "no join is counted for a caller already held");
            counters.Left.Should().Be(0, "and no leave");
            run.Server.Queues.GetByName(Sales).Should().NotBeNull("the refresh still admitted the new queue");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveTheCallerAndTheQueueAsALoadDoes_WhenTheCompletedRefreshNoLongerListsThem()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        var held = run.Server.Queues.GetByName(BootingAsterisk.QueueName)!.Entries[HeldCaller.Channel];
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeNull(
                "a completed listing of every queue that omits one is evidence that it is gone, as in a load");
            held.LeaveNotObserved.Should().BeTrue("its caller left with no leave observed");
            counters.Left.Should().Be(1, "and is counted left once");
            counters.WaitSamples.Should().Be(0, "with no wait sample, since no leave was observed");
            run.Server.Queues.GetByName(Sales).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndNotThrow_WhenAsteriskRefusesQueueStatusAfterSomeRows()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        peer.QueueStatusRefusalAfterRows = BootingAsterisk.PermissionDenied;
        var outcome = await Record.ExceptionAsync(() => run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        AssertNothingRemoved(run, peer, counters, outcome, expectFailure: null);
        run.Server.Queues.GetByName(Sales).Should().BeNull("a refused listing applies none of its rows");
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndThrowOperationCanceled_WhenTheTokenIsCancelledDuringTheRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MainQueueListed = false;
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        using var cts = new CancellationTokenSource();
        try
        {
            var refresh = run.Server.ReconcileQueuesAsync(cts.Token).AsTask();
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the refresh asked");
            await cts.CancelAsync();
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => refresh.WaitAsync(Run.Bound));

            AssertNothingRemoved(run, peer, counters, outcome, expectFailure: typeof(OperationCanceledException));
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRemoveNothingAndThrowNotConnected_WhenTheSessionEndsDuringTheRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        using var counters = new LiveCounters();

        peer.MainQueueListed = false;
        peer.Close = PeerClose.WhenAsked;
        peer.CloseWhenAsked = "QueueStatus";
        var outcome = await Record.ExceptionAsync(() => run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeOfType<AmiNotConnectedException>("a read its session cut short reconciles nothing");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName)?.Entries.Should().ContainKey(HeldCaller.Channel);
            run.Server.Queues.GetByName(BootingAsterisk.QueueName)?.Members.Should().ContainKey(BootingAsterisk.MemberLocation);
            counters.Left.Should().Be(0, "no caller is counted left");
            run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0, "the read's window is closed");
        }
    }

    private static void AssertNothingRemoved(Run run, BootingAsterisk peer, LiveCounters counters, Exception? outcome,
        Type? expectFailure)
    {
        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            if (expectFailure is null)
                outcome.Should().BeNull("a refused QueueStatus does not make the refresh throw");
            else
                outcome.Should().BeAssignableTo(expectFailure);
            queue.Should().NotBeNull("an unfinished response is no evidence that the queue is gone");
            queue?.Entries.Should().ContainKey(HeldCaller.Channel, "nor that its caller left");
            queue?.Members.Should().ContainKey(BootingAsterisk.MemberLocation, "nor that its member was removed");
            counters.Left.Should().Be(0, "no caller is counted left");
            run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0, "the read's window is closed");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    private static AskCounts Asks(BootingAsterisk peer) => new(peer.Asked("Status"), peer.Asked("BridgeList"),
        peer.Asked("ConfbridgeListRooms"), peer.Asked("QueueStatus"), peer.Asked("Agents"));

    private sealed record AskCounts(int Status, int BridgeList, int ConfbridgeListRooms, int QueueStatus, int Agents);

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
}

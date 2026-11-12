using System.Collections.Concurrent;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// An event that names a queue Live does not hold makes Live ask for that queue alone (<c>QueueStatus</c> with its
/// <c>Queue</c>) and admit its configuration and members, outside the dispatch that delivered the event. The event keeps
/// its ordinary effect, once. The targeted answer removes nothing of another queue, brings back no caller or member seen
/// leaving after it was asked, and repeated events naming the same queue on the same AMI session share one listing that
/// ends with its session or with the server.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that has finished starting and that honours the <c>Queue</c>
/// header as Asterisk does. A test waits for a discovery on the peer's count of its ask, then on
/// <see cref="VerbaraServer.PendingQueueDiscovery"/>, which the discovery registers before it sends. In the counter
/// collection, because the tests count joins exactly.
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class VerbaraServerQueueDiscoveryTests
{
    private const string Sales = "sales";
    private const string CallerUid = "1790633900.21";
    private const string CallerChannel = "PJSIP/far-00000021";
    private static readonly QueueCaller Caller = new(CallerChannel, CallerUid, Position: 1, Wait: 0);
    private static readonly QueueCaller HeldCaller = new("PJSIP/far-00000001", "1790633900.1", Position: 1, Wait: 2);

    [Fact]
    public async Task QueueCallerJoin_ShouldAdmitTheQueueConfigurationAndMembersAndCountTheJoinOnce_WhenItNamesAQueueCreatedAfterTheLoad()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001", "PJSIP/2002"], [Caller])];
        using var counters = new LiveCounters();
        var joined = new ConcurrentQueue<string>();
        run.Server.Queues.CallerJoined += (queue, entry) => joined.Enqueue($"{queue}/{entry.Channel}");

        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        var sales = run.Server.Queues.GetByName(Sales);
        using (new AssertionScope())
        {
            discovered.Should().BeTrue("an event naming an unknown queue asks for that queue");
            peer.QueueStatusFilters.Should().Equal([null, Sales], "the load's listing, then one listing of that queue alone");
            sales?.Strategy.Should().Be("rrmemory", "the queue's configuration is admitted from its listing");
            sales?.Members.Keys.Should().BeEquivalentTo(["PJSIP/2001", "PJSIP/2002"], "and its members");
            sales?.Entries.Keys.Should().Equal([CallerChannel], "the joining caller is held once");
            sales?.Entries[CallerChannel].FromSnapshot.Should().BeFalse("as the live join it is");
            counters.Joined.Should().Be(1, "the live event keeps its ordinary effect, once");
            joined.Should().StartWith($"{Sales}/{CallerChannel}", "the live join is announced first, as always");
            run.Server.PendingQueueDiscoveryCount.Should().Be(0, "the finished discovery releases its entry");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldLeaveTheOtherQueueItsMembersAndCallers_WhenTheTargetedListingOmitsIt()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, QueueEntries = [HeldCaller] };
        await using var run = await Run.StartAsync(peer);
        var other = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using var counters = new LiveCounters();

        // Were this a listing of every queue, its silence about rqq would remove it: it is not.
        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            peer.QueueStatusFilters.Should().Equal([null, Sales], "the discovery asks for the named queue alone");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeSameAs(other,
                "a targeted listing that says nothing of another queue removes nothing of it");
            other?.Members.Should().ContainKey(BootingAsterisk.MemberLocation, "its member is kept");
            other?.Entries.Should().ContainKey(HeldCaller.Channel, "and its waiting caller");
            counters.Left.Should().Be(0, "no caller is counted left");
            run.Server.Queues.GetByName(Sales)?.Strategy.Should().Be("rrmemory");
        }
    }

    [Fact]
    public async Task QueueCallerLeave_ShouldNotBeUndoneByTheTargetedListing_WhenTheCallerAndAMemberLeaveWhileItIsRead()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001", "PJSIP/2002"], [Caller])];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        using var counters = new LiveCounters();
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the discovery asked");

            // Asterisk reports them after the request, and its answer, read before they happened, still lists both.
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2002"));
            await peer.SendEventAsync("QueueMemberRemoved", AsteriskFrames.QueueMember(Sales, "PJSIP/2002"));
            await peer.SendEventAsync("QueueCallerLeave", AsteriskFrames.QueueCallerLeave(CallerUid, CallerChannel, Sales, 1, 0));
            answer.SetResult();
            var finished = await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(Sales));

            var sales = run.Server.Queues.GetByName(Sales);
            using (new AssertionScope())
            {
                finished.Should().BeTrue();
                sales?.Strategy.Should().Be("rrmemory", "the listing still admits the configuration");
                sales?.Entries.Should().NotContainKey(CallerChannel, "a caller seen leaving after the request stays gone");
                sales?.Members.Should().NotContainKey("PJSIP/2002", "a member seen removed after the request stays gone");
                sales?.Members.Should().ContainKey("PJSIP/2001", "the member nothing changed is admitted");
                counters.Joined.Should().Be(1, "one join, the live one");
                counters.Left.Should().Be(1, "one leave, the observed one");
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueEvents_ShouldShareOneTargetedListing_WhenSeveralNameTheSameUnknownQueueWhileItIsInFlight()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Caller])];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Queues.CallerJoined += (queue, _) =>
        {
            if (string.Equals(queue, BootingAsterisk.QueueName, StringComparison.Ordinal))
                sentinel.TrySetResult();
        };
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 2));
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("SALES", "PJSIP/2009"));
            // A join in a held queue, after the three: its handler runs once the dispatch has handled them.
            await peer.SendEventAsync("QueueCallerJoin",
                AsteriskFrames.QueueCallerJoin("1790633900.30", "PJSIP/far-00000030", BootingAsterisk.QueueName, 1, 1));
            (await CompletesWithinBoundAsync(sentinel.Task)).Should().BeTrue("the dispatch handled every event");
            var inFlight = run.Server.PendingQueueDiscoveryCount;
            answer.SetResult();
            var finished = await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(Sales));

            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 1));
            await peer.SendEventAsync("QueueCallerJoin",
                AsteriskFrames.QueueCallerJoin("1790633900.31", "PJSIP/far-00000031", BootingAsterisk.QueueName, 2, 2));
            using (new AssertionScope())
            {
                inFlight.Should().Be(1, "three events naming one unknown queue, in any letter case, share one discovery");
                finished.Should().BeTrue();
                peer.QueueStatusFilters.Should().Equal([null, Sales],
                    "one targeted listing, and none for an event naming a queue Live holds by then");
                run.Server.Queues.GetByName(Sales)?.Members.Keys.Should().Contain(["PJSIP/2001", "PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldEndItsDiscoveryAndKeepNoEntry_WhenTheAmiSessionEndsWhileItIsInFlight()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the discovery asked");
            var inFlight = run.Server.PendingQueueDiscovery(Sales);

            peer.CloseSession();
            var ended = await CompletesWithinBoundAsync(inFlight);

            using (new AssertionScope())
            {
                ended.Should().BeTrue("the discovery ends with the AMI session it was asked on");
                inFlight.IsFaulted.Should().BeFalse("its ending is absorbed, not left unobserved");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0, "no request stays attached to a retired session");
                run.Server.Queues.GetByName(Sales)?.Strategy.Should().BeNull(
                    "a listing that never completed admits no configuration");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldEndItsDiscoveryAndApplyNothing_WhenTheServerIsDisposedWhileItIsInFlight()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the discovery asked");
            var inFlight = run.Server.PendingQueueDiscovery(Sales);

            var disposed = await CompletesWithinBoundAsync(run.Server.DisposeAsync().AsTask());
            var ended = inFlight.IsCompleted;
            answer.SetResult();

            using (new AssertionScope())
            {
                disposed.Should().BeTrue("disposal does not wait for an answer that may never come");
                ended.Should().BeTrue("the discovery ended by the time disposal returned");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
                run.Server.Queues.GetByName(Sales)?.Strategy.Should().BeNull("nothing is admitted into a disposed server");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldInventNoConfiguration_WhenTheTargetedListingNamesNoQueue()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);

        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, "ghost", 1, 1));
        var discovered = await DiscoveredAsync(run, peer, "ghost");

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            peer.QueueStatusFilters.Should().Equal([null, "ghost"]);
            run.Server.Queues.GetByName("ghost")?.Strategy.Should().BeNull(
                "an answer that lists no queue gives none a configuration");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().NotBeNull("and removes nothing of another");
        }
    }

    /// <summary>Waits for the peer to read the discovery's ask, then for the discovery to finish.</summary>
    private static async Task<bool> DiscoveredAsync(Run run, BootingAsterisk peer, string queue) =>
        await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))
        && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(queue));

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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// An event that names a queue Live holds no completed listing of makes Live ask for that queue alone (<c>QueueStatus</c>
/// with its <c>Queue</c>) and admit its configuration, members and waiting callers, outside the dispatch that delivered
/// the event. The event keeps its ordinary effect, once. The targeted answer only admits: it removes nothing, raises no
/// snapshot completion, ignores rows of another queue and brings back no caller or member seen leaving after it was
/// asked. Repeated events naming the same queue on one AMI session share one listing, which ends with its session or with
/// the server; a listing that did not complete is asked again at the next event, and one that listed nothing is not, on
/// that session. The agents whose memberships it admitted or changed are settled.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that has finished starting and that honours the <c>Queue</c>
/// header as Asterisk does. A test waits for a discovery on the peer's count of its ask, then on
/// <see cref="VerbaraServer.PendingQueueDiscovery"/>, which the discovery registers on the dispatch, before it sends. In
/// the counter collection, because the tests count joins exactly.
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
    public async Task QueueCallerJoin_ShouldAdmitTheQueueAndHoldTheJoiningCallerOnce_WhenItNamesAQueueCreatedAfterTheLoad()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001", "PJSIP/2002"], [Caller])];
        using var counters = new LiveCounters();
        var joined = new ConcurrentQueue<string>();
        var completions = 0;
        run.Server.Queues.CallerJoined += (queue, entry) => joined.Enqueue($"{queue}/{entry.Channel}");
        run.Server.Queues.QueueSnapshotCompleted += _ => Interlocked.Increment(ref completions);
        var fenceWaits = 0;
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list == "QueueStatus")
                Interlocked.Increment(ref fenceWaits);
        };

        // The caller is in its own listing: Asterisk answers with the caller whose join triggered the ask.
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        var sales = Held(run, Sales);
        using (new AssertionScope())
        {
            discovered.Should().BeTrue("an event naming an unknown queue asks for that queue");
            peer.QueueStatusFilters.Should().Equal([null, Sales], "the load's listing, then one listing of that queue alone");
            sales.Strategy.Should().Be("rrmemory", "the queue's configuration is admitted from its listing");
            sales.Members.Keys.Should().BeEquivalentTo(["PJSIP/2001", "PJSIP/2002"], "and its members");
            sales.Entries.Keys.Should().Equal([CallerChannel], "the joining caller is held once");
            sales.Entries[CallerChannel].FromSnapshot.Should().BeFalse("as the live join it is");
            counters.Joined.Should().Be(1, "the live event keeps its ordinary effect, and its join is counted once");
            joined.Should().StartWith($"{Sales}/{CallerChannel}", "the live join is announced first, as always");
            Volatile.Read(ref completions).Should().Be(0, "a targeted listing raises no snapshot completion");
            run.Server.PendingQueueDiscoveryCount.Should().Be(0, "the finished discovery releases its entry");
            Volatile.Read(ref fenceWaits).Should().Be(1,
                "the listing runs outside the dispatch, so it waits for the dispatch instead of skipping the wait");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAdmitTheCallersAlreadyWaitingOnceWithTheirListedIdentity_WhenItNamesANewQueue()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var first = new QueueCaller("PJSIP/far-00000031", "1790633900.31", Position: 1, Wait: 12);
        var second = new QueueCaller("PJSIP/far-00000032", "1790633900.32", Position: 2, Wait: 5);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [first, second])];
        using var counters = new LiveCounters();

        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        var sales = Held(run, Sales);
        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            sales.Entries.Keys.Should().BeEquivalentTo([first.Channel, second.Channel], "each listed caller is held once");
            foreach (var caller in new[] { first, second })
            {
                var entry = sales.Entries[caller.Channel];
                entry.UniqueId.Should().Be(caller.UniqueId, "with the Uniqueid the listing reports");
                entry.Position.Should().Be(caller.Position, "and its position");
                entry.FromSnapshot.Should().BeTrue("its join instant comes from the listing");
                entry.ReportedWaitSeconds.Should().Be(caller.Wait, "derived from the wait the listing reports");
            }
            counters.Joined.Should().Be(2, "each admitted caller is counted joined once");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldLeaveTheOtherQueueItsVisitAndCounts_WhenTheTargetedListingOmitsIt()
    {
        const string OtherUid = "1790633900.41";
        const string OtherChannel = "PJSIP/far-00000041";
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance, new NullStore(), run.Clock);
        using var tracker = new QueueSessionTracker(manager, Options.Create(new SessionOptions()));
        manager.AttachToServer(run.Server, "srv-queue-discovery");
        var left = new ConcurrentQueue<QueueVisitSignal>();
        manager.VisitLeft += left.Enqueue;

        // A caller waits in the load's queue, with an open visit.
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(OtherUid, OtherChannel, OtherUid));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(OtherUid, OtherChannel, BootingAsterisk.QueueName, 1, 1));
        await DispatchedAsync(run, peer, "visit-open");
        var other = Held(run, BootingAsterisk.QueueName);
        var visits = tracker.GetByQueueName(BootingAsterisk.QueueName);
        visits.Should().NotBeNull("the tracker counts the load's queue");
        var (waiting, abandoned) = (visits!.CallsWaiting, visits.CallsAbandoned);
        waiting.Should().Be(1, "the caller's visit is open");
        using var counters = new LiveCounters();

        // Were this a listing of every queue that completed, its silence about rqq would remove it and end the visit.
        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var discovered = await DiscoveredAsync(run, peer, Sales);
        await DispatchedAsync(run, peer, "after-discovery");

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            peer.QueueStatusFilters.Should().Equal([null, Sales], "the discovery asks for the named queue alone");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().BeSameAs(other,
                "a targeted listing that says nothing of another queue removes nothing of it");
            other.Members.Should().ContainKey(BootingAsterisk.MemberLocation, "its member is kept");
            other.Entries.Should().ContainKey(OtherChannel, "and its waiting caller");
            left.Should().BeEmpty("the open visit of the other queue is not ended");
            visits.CallsWaiting.Should().Be(waiting, "its queue's waiting count is unchanged");
            visits.CallsAbandoned.Should().Be(abandoned, "and its abandonment count");
            counters.Left.Should().Be(0, "no caller is counted left");
            Held(run, Sales).Strategy.Should().Be("rrmemory");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldIgnoreTheRowsOfAnotherQueue_WhenTheTargetedAnswerCarriesThem()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var stranger = new QueueCaller("PJSIP/far-00000051", "1790633900.51", Position: 1, Wait: 3);
        peer.IgnoresQueueFilter = true;
        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue("support", ["PJSIP/3001"], [stranger]), new ListedQueue(Sales, ["PJSIP/2001"], [])];
        using var counters = new LiveCounters();

        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            Held(run, Sales).Members.Keys.Should().Equal(["PJSIP/2001"], "the named queue is admitted");
            run.Server.Queues.GetByName("support").Should().BeNull("a row of another queue in the answer is ignored");
            counters.Joined.Should().Be(0, "no caller of another queue is admitted");
            Held(run, BootingAsterisk.QueueName).Members.Should().ContainKey(BootingAsterisk.MemberLocation,
                "and no queue the answer omits is touched");
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
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
            var inFlight = run.Server.PendingQueueDiscovery(Sales);

            // Asterisk reports them after the request, and its answer, read before they happened, still lists both.
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2002"));
            await peer.SendEventAsync("QueueMemberRemoved", AsteriskFrames.QueueMember(Sales, "PJSIP/2002"));
            await peer.SendEventAsync("QueueCallerLeave", AsteriskFrames.QueueCallerLeave(CallerUid, CallerChannel, Sales, 1, 0));
            answer.SetResult();
            var finished = await CompletesWithinBoundAsync(inFlight);

            var sales = Held(run, Sales);
            using (new AssertionScope())
            {
                finished.Should().BeTrue();
                sales.Strategy.Should().Be("rrmemory", "the listing still admits the configuration");
                sales.Entries.Should().NotContainKey(CallerChannel, "a caller seen leaving after the request stays gone");
                sales.Members.Should().NotContainKey("PJSIP/2002", "a member seen removed after the request stays gone");
                sales.Members.Should().ContainKey("PJSIP/2001", "the member nothing changed is admitted");
                counters.Joined.Should().Be(1, "one join, the live one");
                counters.Left.Should().Be(1, "one leave, the observed one");
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task Hangup_ShouldNotBeUndoneByTheTargetedListing_WhenItArrivesAfterTheListingReadTheCallersRow()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var waiting = new QueueCaller("PJSIP/far-00000061", "1790633900.61", Position: 1, Wait: 9);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [waiting])];
        // Asterisk wrote the caller's row, then the caller hung up, then the listing completed.
        peer.BeforeQueueStatusComplete = () =>
            peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(waiting.UniqueId, waiting.Channel, waiting.UniqueId));
        using var counters = new LiveCounters();

        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var discovered = await DiscoveredAsync(run, peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            Held(run, Sales).Entries.Should().BeEmpty("a row read before the caller's Hangup does not bring it back");
            run.Server.Queues.HeldUniqueIdCounts().Should().NotContainKey(waiting.UniqueId, "and the record does not count it");
            counters.Joined.Should().Be(0);
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }
    }

    [Fact]
    public async Task QueueEvents_ShouldShareOneTargetedListing_WhenSeveralNameTheSameUnknownQueueInAnyLetterCase()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Caller])];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 2));
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("SALES", "PJSIP/2009"));
            await DispatchedAsync(run, peer, "three-events");
            var inFlight = run.Server.PendingQueueDiscoveryCount;
            var pending = run.Server.PendingQueueDiscovery("Sales");
            answer.SetResult();
            var finished = await CompletesWithinBoundAsync(pending);

            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 1));
            await DispatchedAsync(run, peer, "after-listing");
            using (new AssertionScope())
            {
                inFlight.Should().Be(1, "three events naming one unknown queue, in any letter case, share one discovery");
                finished.Should().BeTrue();
                peer.QueueStatusFilters.Should().Equal([null, Sales],
                    "one targeted listing, and none for an event naming a queue Live holds a listing of by then");
                Held(run, Sales).Members.Keys.Should().Contain(["PJSIP/2001", "PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAskAgain_WhenTheFirstTargetedListingWasRefused()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.MainQueueListed = false;
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        peer.QueueStatusRefusalAfterRows = BootingAsterisk.PermissionDenied;

        await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember(Sales, "PJSIP/2001"));
        var refused = await DiscoveredAsync(run, peer, Sales);
        var configuredAfterRefusal = run.Server.Queues.GetByName(Sales)?.Strategy is not null;

        peer.QueueStatusRefusalAfterRows = null;
        await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 2));
        var second = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 3))
            && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(Sales));

        using (new AssertionScope())
        {
            refused.Should().BeTrue("the first event asked");
            configuredAfterRefusal.Should().BeFalse("a refused listing admits nothing");
            second.Should().BeTrue("the next event naming the still-unlisted queue asks again");
            peer.QueueStatusFilters.Should().Equal([null, Sales, Sales]);
            Held(run, Sales).Strategy.Should().Be("rrmemory", "the second listing admits the queue");
            run.Server.PendingQueueDiscoveryCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldInventNoConfigurationAndNotAskAgainOnTheSession_WhenTheTargetedListingNamesNoQueue()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);

        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, "ghost", 1, 1));
        var discovered = await DiscoveredAsync(run, peer, "ghost");
        await peer.SendEventAsync("QueueCallerLeave", AsteriskFrames.QueueCallerLeave(CallerUid, CallerChannel, "ghost", 1, 0));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin("1790633900.22", "PJSIP/far-00000022", "GHOST", 1, 1));
        await DispatchedAsync(run, peer, "ghost-again");

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            run.Server.Queues.GetByName("ghost")?.Strategy.Should().BeNull(
                "an answer that lists no row of the queue gives it no configuration");
            run.Server.Queues.GetByName(BootingAsterisk.QueueName).Should().NotBeNull("and removes nothing of another");
            peer.QueueStatusFilters.Should().Equal([null, "ghost"],
                "a completed listing with no row of the queue is not asked again on that session, in any letter case");
            run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
        }

        // A completed listing of every queue clears the marker: the next event asks again.
        await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin("1790633900.23", "PJSIP/far-00000023", "ghost", 2, 2));
        var askedAgain = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 4))
            && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("ghost"));
        askedAgain.Should().BeTrue("after a completed listing of every queue, the next event naming it asks again");
        peer.QueueStatusFilters.Should().Equal([null, "ghost", null, "ghost"]);
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldAskAgainOnTheNewSession_WhenTheListingOfTheOldOneNamedNoQueue()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        // The new session's reload has its QueueStatus refused, so no completed listing of every queue clears the marker:
        // only the new session does. A refused listing still ends the reload's queue step, so the session may ask.
        var second = new BootingAsterisk { BootedAtLogin = true, QueueStatusRefusalAfterRows = BootingAsterisk.PermissionDenied };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        await first.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, "ghost", 1, 1));
        var discovered = await DiscoveredAsync(run, first, "ghost");

        var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 2);
        first.CloseSession();
        await run.SecondSocketCreated.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        var loaded = await CompletesWithinBoundAsync(reloaded);
        await second.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin("1790633900.24", "PJSIP/far-00000024", "ghost", 1, 1));
        var askedOnNewSession = await DiscoveredAsync(run, second, "ghost");

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            loaded.Should().BeTrue("the new session's reload completes, its queue listing refused");
            askedOnNewSession.Should().BeTrue("a new AMI session holds no marker of the old one");
            second.QueueStatusFilters.Should().Equal([null, "ghost"]);
            run.Server.PendingQueueDiscoveryCount.Should().Be(0);
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
            var countWhileInFlight = run.Server.PendingQueueDiscoveryCount;

            peer.CloseSession();
            var ended = await CompletesWithinBoundAsync(inFlight);

            using (new AssertionScope())
            {
                countWhileInFlight.Should().Be(1, "the discovery was registered");
                ended.Should().BeTrue("the discovery ends with the AMI session it was asked on");
                inFlight.IsFaulted.Should().BeFalse("its ending is absorbed, not left unobserved");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0, "no request stays attached to a retired session");
                run.Server.Queues.GetByName(Sales)?.Strategy.Should().BeNull("a listing that never completed admits no configuration");
                run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0, "its read window is closed");
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
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
            var countWhileInFlight = run.Server.PendingQueueDiscoveryCount;

            var disposed = await CompletesWithinBoundAsync(run.Server.DisposeAsync().AsTask());
            var ended = inFlight.IsCompleted;
            answer.SetResult();

            using (new AssertionScope())
            {
                countWhileInFlight.Should().Be(1, "the discovery was registered");
                disposed.Should().BeTrue("disposal does not wait for an answer that may never come");
                ended.Should().BeTrue("the discovery ended by the time disposal returned");
                inFlight.IsFaulted.Should().BeFalse();
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
    public async Task QueueMemberAdded_ShouldMakeThePausedAgentAvailableOnce_WhenTheTargetedListingAdmitsAnUnpausedMembership()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue("support", ["Agent/1001"], [], PausedMembers: ["Agent/1001"]));
        rig.State().Should().Be(AgentState.Paused);
        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["Agent/1001"], [])];

        await rig.Peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001"));
        var discovered = await DiscoveredAsync(rig.Run, rig.Peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            Held(rig.Run, Sales).Members.Should().ContainKey("Agent/1001");
            rig.State().Should().Be(AgentState.Available, "an unpaused membership makes the agent available");
            rig.Changes.Should().Be(1, "exactly one state-changed notification");
            rig.Peer.Asked("Agents").Should().Be(rig.AgentsAsked, "no agents listing is asked");
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldPauseTheAgentOnce_WhenTheTargetedListingAdmitsItsOnlyMembershipPaused()
    {
        await using var rig = await AgentRig.StartAsync();
        rig.State().Should().Be(AgentState.Available, "an agent with no membership is available");
        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["Agent/1001"], [], PausedMembers: ["Agent/1001"])];

        await rig.Peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001", paused: true));
        var discovered = await DiscoveredAsync(rig.Run, rig.Peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            rig.State().Should().Be(AgentState.Paused, "its only membership is paused");
            rig.Changes.Should().Be(1, "exactly one state-changed notification");
            rig.Peer.Asked("Agents").Should().Be(rig.AgentsAsked, "no agents listing is asked");
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldLeaveTheAgentOnCall_WhenTheTargetedListingAdmitsAMembership()
    {
        await using var rig = await AgentRig.StartAsync();
        rig.Run.Server.Agents.OnAgentConnect("1001", "PJSIP/far-00000009");
        var before = rig.Changes;
        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["Agent/1001"], [], PausedMembers: ["Agent/1001"])];

        await rig.Peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001", paused: true));
        var discovered = await DiscoveredAsync(rig.Run, rig.Peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue();
            Held(rig.Run, Sales).Members.Should().ContainKey("Agent/1001");
            rig.State().Should().Be(AgentState.OnCall, "an agent on a call keeps its state");
            rig.Changes.Should().Be(before, "no state-changed notification");
            rig.Peer.Asked("Agents").Should().Be(rig.AgentsAsked);
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldStillSettleTheAdmittedMembership_WhenAMemberAddedSubscriberThrows()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue("support", ["Agent/1001"], [], PausedMembers: ["Agent/1001"]));
        rig.Run.Server.Queues.MemberAdded += (queue, _) =>
        {
            if (queue == Sales)
                throw new InvalidOperationException("member-added subscriber");
        };
        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["Agent/1001"], [])];

        await rig.Peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001"));
        var discovered = await DiscoveredAsync(rig.Run, rig.Peer, Sales);

        using (new AssertionScope())
        {
            discovered.Should().BeTrue("the failing subscriber does not leave the discovery hanging");
            Held(rig.Run, Sales).Members.Should().ContainKey("Agent/1001", "the row was committed before the drain");
            rig.State().Should().Be(AgentState.Available, "the committed membership is settled although the drain threw");
            rig.Changes.Should().Be(1);
            rig.Run.Server.PendingQueueDiscovery(Sales).IsFaulted.Should().BeFalse("the failure is logged, not left unobserved");
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldAskOnlyTheLoadsListing_WhenQueueEventsArriveBeforeTheLoadListedTheQueues()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OtherQueues =
        [
            new ListedQueue("q1", ["PJSIP/2001"], []), new ListedQueue("q2", ["PJSIP/2002"], []),
            new ListedQueue("q3", ["PJSIP/2003"], []),
        ];
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Status"] = status.Task };
        await using var run = await Run.ConnectAsync(peer);
        try
        {
            // The first load is held on its Status, before it asks QueueStatus: the table holds no queue yet.
            var start = run.StartServerAsync();
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            foreach (var queue in new[] { BootingAsterisk.QueueName, "q1", "q2", "q3" })
                await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(queue, "PJSIP/2001", status: 2));
            await DispatchedAsync(run, peer, "during-load");
            var pendingDuringLoad = run.Server.PendingQueueDiscoveryCount;
            status.SetResult();
            await start;
            await DispatchedAsync(run, peer, "after-load");
            var afterLoad = peer.QueueStatusFilters;

            // A queue Asterisk creates after the load is still found, once.
            peer.OtherQueues = [.. peer.OtherQueues, new ListedQueue("q4", ["PJSIP/2004"], [])];
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("q4", "PJSIP/2004"));
            var discovered = await DiscoveredAsync(run, peer, "q4");

            using (new AssertionScope())
            {
                pendingDuringLoad.Should().Be(0, "no queue is asked for alone before the load lists the queues");
                afterLoad.Should().Equal([null], "the load's listing of every queue covers the queues the events named");
                foreach (var queue in new[] { "q1", "q2", "q3" })
                    Held(run, queue).Strategy.Should().Be("rrmemory", "the load listed {0}", queue);
                discovered.Should().BeTrue("a queue created after the load is still asked for");
                peer.QueueStatusFilters.Should().Equal([null, "q4"], "once, by name");
                Held(run, "q4").Members.Keys.Should().Equal(["PJSIP/2004"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            status.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldWaitForTheRefreshInFlightAndAskOnlyForWhatItDidNotList_WhenItArrivesDuringIt()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        try
        {
            var refresh = run.Server.ReconcileQueuesAsync().AsTask();
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the refresh asked");

            // Both named while the refresh's listing of every queue is read: sales is in it, ghost is not.
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "PJSIP/2001", status: 2));
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember("ghost", "PJSIP/2009", status: 2));
            await DispatchedAsync(run, peer, "during-refresh");
            var askedDuringRefresh = peer.Asked("QueueStatus");
            var pendingDuringRefresh = run.Server.PendingQueueDiscoveryCount;
            answer.SetResult();
            await refresh.WaitAsync(Run.Bound);
            var askedAfter = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 3))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("ghost"));
            await DispatchedAsync(run, peer, "after-refresh");
            var afterFirstRefresh = peer.QueueStatusFilters;

            // A second refresh lists every queue again: a name the first one's wait handed on is not asked for again.
            await run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);
            var pendingAfterSecondRefresh = run.Server.PendingQueueDiscoveryCount;
            await run.Server.PendingQueueDiscovery("ghost").WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                askedDuringRefresh.Should().Be(2, "no queue is asked for alone while a listing of every queue is read");
                pendingDuringRefresh.Should().Be(0, "nor registered");
                askedAfter.Should().BeTrue("a named queue the completed listing did not list is asked for once it ends");
                afterFirstRefresh.Should().Equal([null, null, "ghost"],
                    "the load's listing, the refresh's, and the one queue the refresh did not list");
                pendingAfterSecondRefresh.Should().Be(0, "no event named ghost since its listing");
                peer.QueueStatusFilters.Should().Equal([null, null, "ghost", null]);
                Held(run, Sales).Strategy.Should().Be("rrmemory", "the refresh listed sales");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueCallerJoin_ShouldWaitForTheDispatchThatNamedTheQueue_WhenAnotherSubscriberStillHoldsThatDispatch()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [Caller])];
        var fenceWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list == "QueueStatus")
                fenceWait.TrySetResult();
        };
        // Subscribed after the server, so it runs in the dispatch the server's handler named the queue in.
        using var hold = new JoinHold();
        using var subscription = run.Connection.Subscribe(hold);
        try
        {
            await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(CallerUid, CallerChannel, Sales, 1, 1));
            await hold.Held.WaitAsync(Run.Bound);
            (await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the discovery asked");
            var pending = run.Server.PendingQueueDiscovery(Sales);
            var first = await Task.WhenAny(fenceWait.Task, pending).WaitAsync(Run.Bound);
            var finishedWhileHeld = pending.IsCompleted;
            var waitedWhileHeld = fenceWait.Task.IsCompleted;

            hold.Release();
            var finished = await CompletesWithinBoundAsync(pending);

            using (new AssertionScope())
            {
                waitedWhileHeld.Should().BeTrue(
                    "the discovery carries none of the dispatch's context, so it waits for that dispatch to end");
                finishedWhileHeld.Should().BeFalse("it applies nothing while the dispatch that named the queue runs");
                first.Should().BeSameAs(fenceWait.Task);
                finished.Should().BeTrue();
                Held(run, Sales).Entries.Keys.Should().Equal([CallerChannel], "the joining caller is held once");
                HeldCallerRecord.AssertEqualsTable(run.Server.Queues);
            }
        }
        finally
        {
            hold.Release();
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldKeepTheNewSessionsDiscovery_WhenTheOldSessionsDiscoveryEndsAfterIt()
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true, Agents = [new ListedAgent("1001")],
            OtherQueues = [new ListedQueue("support", ["Agent/1001"], [], PausedMembers: ["Agent/1001"])],
        };
        var second = new BootingAsterisk { BootedAtLogin = true, Agents = [new ListedAgent("1001")] };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var oldEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSettling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = 0;
        try
        {
            await run.StartServerAsync();
            run.Server.Agents.GetById("1001")?.State.Should().Be(AgentState.Paused);
            // The old session's discovery settles agent 1001, whose handler holds it there: it ends after the new one asks.
            run.Server.Agents.AgentStateChanged += agent =>
            {
                if (agent.AgentId == "1001" && Interlocked.Exchange(ref holding, 1) == 0)
                {
                    oldSettling.TrySetResult();
                    oldEnds.Task.Wait(Run.Bound);
                }
            };
            first.OtherQueues = [.. first.OtherQueues, new ListedQueue(Sales, ["Agent/1001"], [])];
            await first.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001"));
            await oldSettling.Task.WaitAsync(Run.Bound);
            var old = run.Server.PendingQueueDiscovery(Sales);

            // The session ends; the new one's reload lists neither sales nor support, so both leave the table.
            var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 2);
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await reloaded.WaitAsync(Run.Bound);
            run.Server.Queues.GetByName(Sales).Should().BeNull("the new session's reload did not list sales");

            second.OtherQueues = [new ListedQueue(Sales, ["Agent/1001"], [])];
            second.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
            await second.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001"));
            (await CompletesWithinBoundAsync(second.AskedAtLeast("QueueStatus", 2))).Should().BeTrue("the new session asked");
            var newPending = run.Server.PendingQueueDiscovery(Sales);

            oldEnds.SetResult();
            var oldEnded = await CompletesWithinBoundAsync(old);
            var afterOldEnded = run.Server.PendingQueueDiscoveryCount;
            var newStillHeld = ReferenceEquals(run.Server.PendingQueueDiscovery(Sales), newPending);

            await second.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(Sales, "Agent/1001", status: 2));
            await DispatchedAsync(run, second, "again-on-new-session");
            answer.SetResult();
            var finished = await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(Sales))
                && await CompletesWithinBoundAsync(newPending);

            using (new AssertionScope())
            {
                oldEnded.Should().BeTrue();
                afterOldEnded.Should().Be(1, "the old session's discovery does not release the new session's");
                newStillHeld.Should().BeTrue("the name stays with the new session's discovery");
                finished.Should().BeTrue();
                second.QueueStatusFilters.Should().Equal([null, Sales],
                    "a later event on the new session shares the discovery in flight");
                Held(run, Sales).Members.Should().ContainKey("Agent/1001");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            oldEnds.TrySetResult();
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAskForTheQueueAlone_WhenAQueueHandlerThrewWhileTheStartsListingWasApplied()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, OtherQueues = [new ListedQueue("q1", ["PJSIP/2001"], [])] };
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
        await using var run = await Run.ConnectAsync(peer);
        var thrown = 0;
        run.Server.Queues.MemberAdded += (queue, _) =>
        {
            if (queue == "q1" && Interlocked.Exchange(ref thrown, 1) == 0)
                throw new InvalidOperationException("a queue handler fault");
        };
        try
        {
            var start = run.StartServerAsync();
            await peer.AskedAtLeast("QueueStatus", 1).WaitAsync(Run.Bound);
            // Named while the start's listing is read; that listing does not list it.
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember("ghost", "PJSIP/2008", status: 2));
            await DispatchedAsync(run, peer, "during-listing");
            var pendingDuringListing = run.Server.PendingQueueDiscoveryCount;
            peer.AnsweredAfter = new Dictionary<string, Task>();
            answer.SetResult();
            var startFault = await Record.ExceptionAsync(() => start);
            var ghostAsked = await DiscoveredAsync(run, peer, "ghost");

            peer.OtherQueues = [.. peer.OtherQueues, new ListedQueue("later", ["PJSIP/2009"], [])];
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var laterAsked = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 3))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("later"));

            using (new AssertionScope())
            {
                startFault.Should().NotBeNull("the handler's fault leaves the start");
                startFault?.ToString().Should().Contain("a queue handler fault");
                pendingDuringListing.Should().Be(0, "nothing is asked alone while the start's listing is read");
                ghostAsked.Should().BeTrue("a listing cut short by a handler ends, and the name it left unlisted is asked");
                laterAsked.Should().BeTrue("the session asks for a queue created after the load");
                peer.QueueStatusFilters.Should().Equal([null, "ghost", "later"]);
                Held(run, "later").Members.Keys.Should().Equal(["PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAskForTheQueueAlone_WhenAQueueHandlerThrewWhileTheReconnectReloadsListingWasApplied()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, OtherQueues = [new ListedQueue("q1", ["PJSIP/2001"], [])] };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await run.StartServerAsync();
        var thrown = 0;
        run.Server.Queues.MemberAdded += (queue, _) =>
        {
            if (queue == "q1" && Interlocked.Exchange(ref thrown, 1) == 0)
                throw new InvalidOperationException("a queue handler fault");
        };
        var failed = run.ServerLog.Logged("[LIVE] Reconnect reload failed");

        first.CloseSession();
        await run.SecondSocketCreated.WaitAsync(Run.Bound);
        run.ReleaseSecondPeer();
        var reloadFailed = await CompletesWithinBoundAsync(failed);
        second.OtherQueues = [.. second.OtherQueues, new ListedQueue("later", ["PJSIP/2009"], [])];
        await second.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
        var discovered = await DiscoveredAsync(run, second, "later");

        using (new AssertionScope())
        {
            reloadFailed.Should().BeTrue("the handler's fault ends the reconnect's reload");
            discovered.Should().BeTrue("the reload's listing ended on the new session, which then asks for a new queue");
            second.QueueStatusFilters.Should().Equal([null, "later"]);
            Held(run, "later").Members.Keys.Should().Equal(["PJSIP/2009"]);
            run.Server.PendingQueueDiscoveryCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldAskForTheNamedQueuesAlone_WhenTheStartThrewBeforeItsQueueListing()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            OtherQueues = [new ListedQueue("named", ["PJSIP/2001"], [])],
        };
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Status"] = status.Task };
        await using var run = await Run.ConnectAsync(peer);
        // Raised by the channel step once its Status was applied in full: a subscriber that throws ends the load there.
        run.Server.ChannelSnapshotCompleted += _ => throw new InvalidOperationException("a channel snapshot subscriber fault");
        try
        {
            var start = run.StartServerAsync();
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            await peer.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember("named", "PJSIP/2001", status: 2));
            await DispatchedAsync(run, peer, "before-queue-step");
            var pendingBefore = run.Server.PendingQueueDiscoveryCount;
            peer.AnsweredAfter = new Dictionary<string, Task>();
            status.SetResult();
            var startFault = await Record.ExceptionAsync(() => start);
            var namedAsked = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 1))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("named"));

            peer.OtherQueues = [.. peer.OtherQueues, new ListedQueue("later", ["PJSIP/2009"], [])];
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var laterAsked = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("later"));

            using (new AssertionScope())
            {
                startFault?.ToString().Should().Contain("a channel snapshot subscriber fault", "the channel step threw");
                pendingBefore.Should().Be(0, "nothing is asked alone before the load ended");
                namedAsked.Should().BeTrue("a load that ended before its queue listing opens its session");
                laterAsked.Should().BeTrue();
                peer.QueueStatusFilters.Should().Equal(["named", "later"], "the load never asked for every queue");
                Held(run, "named").Members.Keys.Should().Equal(["PJSIP/2001"]);
                Held(run, "later").Members.Keys.Should().Equal(["PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            status.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAskForTheQueueAlone_WhenAQueueHandlerThrewWhileARefreshsListingWasApplied()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, OtherQueues = [new ListedQueue("q1", ["PJSIP/2001"], [])] };
        await using var run = await Run.ConnectAsync(peer);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(initialState: false);
        var holding = 0;
        // The start's load is held after its Status: no listing of every queue has ended on the session yet.
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list == "Status" && Interlocked.Exchange(ref holding, 1) == 0)
            {
                held.TrySetResult();
                release.Wait(Run.Bound);
            }
        };
        var thrown = 0;
        run.Server.Queues.MemberAdded += (queue, _) =>
        {
            if (queue == "q1" && Interlocked.Exchange(ref thrown, 1) == 0)
                throw new InvalidOperationException("a queue handler fault");
        };
        try
        {
            var start = run.Server.StartAsync();
            await held.Task.WaitAsync(Run.Bound);
            var refreshFault = await Record.ExceptionAsync(() => run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

            peer.OtherQueues = [.. peer.OtherQueues, new ListedQueue("later", ["PJSIP/2009"], [])];
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var discovered = await DiscoveredAsync(run, peer, "later");
            release.Set();
            await start.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                refreshFault?.ToString().Should().Contain("a queue handler fault", "the handler's fault leaves the refresh");
                discovered.Should().BeTrue("a refresh's listing cut short by a handler still ends, which opens the session");
                peer.QueueStatusFilters.Should().Equal([null, "later", null], "the refresh, the queue alone, the start's");
                Held(run, "later").Members.Keys.Should().Equal(["PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldAskForTheQueueAlone_WhenTheStartWasCancelledBeforeItsQueueListing()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AnsweredAfter = new Dictionary<string, Task> { ["Status"] = status.Task };
        await using var run = await Run.ConnectAsync(peer);
        using var cancel = new CancellationTokenSource();
        try
        {
            var start = run.Server.StartAsync(cancel.Token);
            await peer.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            await cancel.CancelAsync();
            var startFault = await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));
            // The peer answers in order: the abandoned Status goes out before anything asked after it.
            peer.AnsweredAfter = new Dictionary<string, Task>();
            status.SetResult();

            peer.OtherQueues = [new ListedQueue("later", ["PJSIP/2009"], [])];
            await peer.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var discovered = await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 1))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("later"));

            using (new AssertionScope())
            {
                startFault.Should().BeAssignableTo<OperationCanceledException>("the start was cancelled during its Status");
                discovered.Should().BeTrue("a start cancelled before its queue listing leaves its session open");
                peer.QueueStatusFilters.Should().Equal(["later"]);
                Held(run, "later").Members.Keys.Should().Equal(["PJSIP/2009"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            status.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldAskOnlyTheReloadsListing_WhenQueueEventsArriveBeforeTheReconnectReloadListedTheQueues()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new BootingAsterisk
        {
            BootedAtLogin = true, OtherQueues = [new ListedQueue("q1", ["PJSIP/2001"], [])],
            AnsweredAfter = new Dictionary<string, Task> { ["Status"] = status.Task },
        };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        try
        {
            await run.StartServerAsync();
            var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 2);
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();

            // The new session's reload is held on its Status: the old session's discovery state no longer applies.
            await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            foreach (var queue in new[] { BootingAsterisk.QueueName, "q1" })
                await second.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember(queue, "PJSIP/2001", status: 2));
            await DispatchedAsync(run, second, "before-reload-queues");
            var pendingBeforeReload = run.Server.PendingQueueDiscoveryCount;
            second.AnsweredAfter = new Dictionary<string, Task>();
            status.SetResult();
            await reloaded.WaitAsync(Run.Bound);
            await DispatchedAsync(run, second, "after-reload");

            using (new AssertionScope())
            {
                pendingBeforeReload.Should().Be(0, "a new session asks for no queue alone before its reload's listing");
                second.QueueStatusFilters.Should().Equal([null], "the reload's listing covers the queues the events named");
                Held(run, "q1").Strategy.Should().Be("rrmemory");
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            status.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberStatus_ShouldNotAskOnTheNewSessionForAQueueTheOldSessionNamed_WhenTheNewReloadEnds()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk
        {
            BootedAtLogin = true, AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task },
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        try
        {
            var start = run.Server.StartAsync();
            await first.AskedAtLeast("QueueStatus", 1).WaitAsync(Run.Bound);
            // Named on the old session while its listing is read: it waits for that listing, which never ends answered.
            await first.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember("ghost", "PJSIP/2001", status: 2));
            await DispatchedAsync(run, first, "old-session");
            var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 1);
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));
            await reloaded.WaitAsync(Run.Bound);
            var pendingAfterReload = run.Server.PendingQueueDiscoveryCount;
            await run.Server.PendingQueueDiscovery("ghost").WaitAsync(Run.Bound);
            await DispatchedAsync(run, second, "after-reload");

            using (new AssertionScope())
            {
                pendingAfterReload.Should().Be(0, "what the old session was waiting for does not carry to the new one");
                second.QueueStatusFilters.Should().Equal([null], "the new session's reload lists every queue, once");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldFollowOnlyTheNewSessionsListings_WhenTheOldSessionsListingEndsAfterTheNewReload()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(initialState: false);
        var holding = 0;
        // The start's listing of every queue, counted on the old session, is held past that session's end.
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list == "QueueStatus" && Interlocked.Exchange(ref holding, 1) == 0)
            {
                held.TrySetResult();
                release.Wait(Run.Bound);
            }
        };
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var start = run.Server.StartAsync();
            await held.Task.WaitAsync(Run.Bound);
            var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 1);
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await reloaded.WaitAsync(Run.Bound);

            // The new session's reload has ended while the old session's listing is still open.
            second.OtherQueues = [new ListedQueue("later", ["PJSIP/2009"], [])];
            await second.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var laterAsked = await DiscoveredAsync(run, second, "later");

            release.Set();
            await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));

            // The old listing has ended; a refresh on the new session still holds discovery back while it is read.
            second.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = answer.Task };
            var refresh = run.Server.ReconcileQueuesAsync().AsTask();
            (await CompletesWithinBoundAsync(second.AskedAtLeast("QueueStatus", 3))).Should().BeTrue("the refresh asked");
            await second.SendEventAsync("QueueMemberStatus", AsteriskFrames.QueueMember("ghost", "PJSIP/2008", status: 2));
            await DispatchedAsync(run, second, "during-refresh");
            var askedDuringRefresh = second.Asked("QueueStatus");
            var pendingDuringRefresh = run.Server.PendingQueueDiscoveryCount;
            second.AnsweredAfter = new Dictionary<string, Task>();
            answer.SetResult();
            await refresh.WaitAsync(Run.Bound);
            var ghostAsked = await CompletesWithinBoundAsync(second.AskedAtLeast("QueueStatus", 4))
                && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery("ghost"));

            using (new AssertionScope())
            {
                laterAsked.Should().BeTrue("the old session's open listing does not hold the new session back");
                askedDuringRefresh.Should().Be(3, "the old listing's end does not uncount the new session's refresh");
                pendingDuringRefresh.Should().Be(0);
                ghostAsked.Should().BeTrue();
                second.QueueStatusFilters.Should().Equal([null, "later", null, "ghost"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            release.Set();
            answer.TrySetResult();
        }
    }

    [Fact]
    public async Task QueueMemberAdded_ShouldStillAskOnTheNewSession_WhenTheOldSessionsLoadEndsBeforeItsQueueStepAfterTheNewReload()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(initialState: false);
        var holding = 0;
        // The start's load is held after its Status, before its queue step, past the old session's end.
        run.Server.FenceWaitObserved = (list, _) =>
        {
            if (list == "Status" && Interlocked.Exchange(ref holding, 1) == 0)
            {
                held.TrySetResult();
                release.Wait(Run.Bound);
            }
        };
        try
        {
            var start = run.Server.StartAsync();
            await held.Task.WaitAsync(Run.Bound);
            var reloaded = run.ServerLog.Logged("[LIVE] State loaded", 1);
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            run.ReleaseSecondPeer();
            await reloaded.WaitAsync(Run.Bound);

            // The old session's load ends, before its queue step, after the new session's reload opened that session.
            release.Set();
            await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));

            second.OtherQueues = [new ListedQueue("later", ["PJSIP/2009"], [])];
            await second.SendEventAsync("QueueMemberAdded", AsteriskFrames.QueueMember("later", "PJSIP/2009"));
            var discovered = await DiscoveredAsync(run, second, "later");

            using (new AssertionScope())
            {
                first.QueueStatusFilters.Should().BeEmpty("the old session's load never reached its queue step");
                discovered.Should().BeTrue("a load whose session ended does not take the discovery back to that session");
                second.QueueStatusFilters.Should().Equal([null, "later"]);
                run.Server.PendingQueueDiscoveryCount.Should().Be(0);
            }
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>Waits for the peer to read the discovery's ask, then for the discovery to finish.</summary>
    private static async Task<bool> DiscoveredAsync(Run run, BootingAsterisk peer, string queue) =>
        await CompletesWithinBoundAsync(peer.AskedAtLeast("QueueStatus", 2))
        && await CompletesWithinBoundAsync(run.Server.PendingQueueDiscovery(queue));

    /// <summary>Sends a user event after the frames sent so far and waits until the connection has dispatched it.</summary>
    private static async Task DispatchedAsync(Run run, BootingAsterisk peer, string marker)
    {
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = run.Connection.Subscribe(new TailObserver(marker, tail));
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", marker)]);
        await tail.Task.WaitAsync(Run.Bound);
    }

    private static AsteriskQueue Held(Run run, string name) =>
        run.Server.Queues.GetByName(name) ?? throw new Xunit.Sdk.XunitException($"Expected queue {name} to be held.");

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

    /// <summary>A started run whose peer lists agent <c>1001</c> logged in, counting its state changes after the load.</summary>
    private sealed class AgentRig : IAsyncDisposable
    {
        private int _changes;

        private AgentRig(BootingAsterisk peer, Run run)
        {
            Peer = peer;
            Run = run;
            AgentsAsked = peer.Asked("Agents");
            run.Server.Agents.AgentStateChanged += agent =>
            {
                if (agent.AgentId == "1001")
                    Interlocked.Increment(ref _changes);
            };
        }

        public BootingAsterisk Peer { get; }

        public Run Run { get; }

        public int AgentsAsked { get; }

        public int Changes => Volatile.Read(ref _changes);

        public static async Task<AgentRig> StartAsync(params ListedQueue[] queues)
        {
            var peer = new BootingAsterisk { BootedAtLogin = true, OtherQueues = queues, Agents = [new ListedAgent("1001")] };
            return new AgentRig(peer, await Run.StartAsync(peer));
        }

        public AgentState? State() => Run.Server.Agents.GetById("1001")?.State;

        public ValueTask DisposeAsync() => Run.DisposeAsync();
    }

    private sealed class TailObserver(string userEvent, TaskCompletionSource reached) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is UserEventEvent { UserEvent: var name } && name == userEvent)
                reached.TrySetResult();
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Holds the dispatch of the first <c>QueueCallerJoin</c> after every earlier subscriber has handled it.</summary>
    private sealed class JoinHold : IObserver<ManagerEvent>, IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new(initialState: false);

        public Task Held => _held.Task;

        public void Release() => _released.Set();

        public void OnNext(ManagerEvent value)
        {
            if (value is not QueueCallerJoinEvent || !_held.TrySetResult())
                return;

            _released.Wait(Run.Bound);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() => _released.Dispose();
    }

    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}

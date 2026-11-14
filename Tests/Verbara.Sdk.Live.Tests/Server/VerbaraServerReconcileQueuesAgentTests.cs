using System.Collections.Concurrent;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.ReconcileQueuesAsync"/> and the agents whose <c>Agent/&lt;id&gt;</c> memberships it adds,
/// removes or changes. A load re-derives every agent's state in its agents step; the queues-only refresh has no such
/// step, so it settles each agent whose memberships it touched itself, as a live member event does: one
/// <see cref="AgentManager.AgentStateChanged"/> for an agent whose derived state changed, none otherwise, an agent on a
/// call untouched, and no <c>Agents</c> listing asked. A subscriber that throws, of the queue table or of the agents,
/// does not leave a committed membership change unsettled.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>, over a <see cref="BootingAsterisk"/> that lists agent <c>1001</c> logged in. In the counter
/// collection, because one test counts leaves.
/// </remarks>
[Collection(LiveCountersGroup.Name)]
public sealed class VerbaraServerReconcileQueuesAgentTests
{
    private const string Sales = "sales";
    private const string Support = "support";
    private const string Agent = "1001";
    private const string OtherAgent = "1002";
    private const string AgentMember = "Agent/1001";
    private const string OtherAgentMember = "Agent/1002";
    private static readonly QueueCaller HeldCaller = new("PJSIP/far-00000001", "1790633900.1", Position: 1, Wait: 2);

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldMakeTheAgentAvailableOnce_WhenTheCompletedRefreshNoLongerListsItsLastPausedMembership()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember, "PJSIP/2001"], [], PausedMembers: [AgentMember]));
        rig.State(Agent).Should().Be(AgentState.Paused, "the load derives a paused agent from its one paused membership");

        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.Run.Server.Queues.GetByName(Sales)!.Members.Should().NotContainKey(AgentMember, "the refresh removed the membership");
            rig.State(Agent).Should().Be(AgentState.Available, "an agent left with no membership is not paused");
            rig.Changes(Agent).Should().Be(1, "exactly one state-changed notification");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldMakeTheAgentAvailableOnce_WhenTheCompletedRefreshRemovesTheQueueOfItsLastPausedMembership()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]));
        rig.State(Agent).Should().Be(AgentState.Paused);

        rig.Peer.OtherQueues = [];
        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.Run.Server.Queues.GetByName(Sales).Should().BeNull("a completed listing of every queue that omits one removes it");
            rig.State(Agent).Should().Be(AgentState.Available, "the membership went with its queue");
            rig.Changes(Agent).Should().Be(1, "exactly one state-changed notification");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldMakeThePausedAgentAvailableOnce_WhenTheRefreshListsAnUnpausedMembershipInANewQueue()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]));
        rig.State(Agent).Should().Be(AgentState.Paused);

        rig.Peer.OtherQueues =
        [
            new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]),
            new ListedQueue(Support, [AgentMember], []),
        ];
        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.Run.Server.Queues.GetByName(Support)!.Members.Should().ContainKey(AgentMember, "the new queue's membership is admitted");
            rig.State(Agent).Should().Be(AgentState.Available, "one unpaused membership makes the agent available");
            rig.Changes(Agent).Should().Be(1, "exactly one state-changed notification");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldPauseTheAgentOnce_WhenTheRefreshListsAPauseLiveMissed()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], []));
        rig.State(Agent).Should().Be(AgentState.Available);

        rig.Peer.OtherQueues = [new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember])];
        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.Run.Server.Queues.GetByName(Sales)!.Members[AgentMember].Paused.Should().BeTrue("the listed pause is applied");
            rig.State(Agent).Should().Be(AgentState.Paused, "its only membership is paused");
            rig.Changes(Agent).Should().Be(1, "exactly one state-changed notification");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldRaiseNoAgentChange_WhenTheRefreshChangesNoMembership()
    {
        await using var rig = await AgentRig.StartAsync(
            new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]),
            new ListedQueue(Support, [AgentMember], [], PausedMembers: [AgentMember]));
        rig.State(Agent).Should().Be(AgentState.Paused);

        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.State(Agent).Should().Be(AgentState.Paused, "nothing it is derived from changed");
            rig.Changes(Agent).Should().Be(0, "no state-changed notification");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldLeaveTheAgentOnCallAndDeriveItsStateAtCompletion_WhenTheRefreshRemovesItsLastUnpausedMembership()
    {
        await using var rig = await AgentRig.StartAsync(
            new ListedQueue(Sales, [AgentMember], []),
            new ListedQueue(Support, [AgentMember], [], PausedMembers: [AgentMember]));
        rig.State(Agent).Should().Be(AgentState.Available);
        rig.Run.Server.Agents.OnAgentConnect(Agent, "PJSIP/far-00000009");
        rig.State(Agent).Should().Be(AgentState.OnCall);
        var before = rig.Changes(Agent);

        rig.Peer.OtherQueues = [new ListedQueue(Sales, [], []), new ListedQueue(Support, [AgentMember], [], PausedMembers: [AgentMember])];
        await rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.Run.Server.Queues.GetByName(Sales)!.Members.Should().NotContainKey(AgentMember);
            rig.State(Agent).Should().Be(AgentState.OnCall, "an agent on a call keeps its state");
            rig.Changes(Agent).Should().Be(before, "no state-changed notification for it");
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }

        rig.Run.Server.Agents.OnAgentComplete(Agent);
        rig.State(Agent).Should().Be(AgentState.Paused, "at completion its state follows the memberships the refresh left");
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldSettleWhatItAdmittedAndRemoveNothing_WhenItsWaitForThePumpRunsOut()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]));
        var run = rig.Run;
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        var barrierClock = new FakeTimeProvider(run.Clock.GetUtcNow());
        run.Server.BarrierClock = barrierClock;
        try
        {
            await rig.Peer.SendEventAsync("UserEvent", DispatchGate.Frame());
            await gate.Held.WaitAsync(Run.Bound);

            // Sales, and its paused membership, are omitted: a refresh that removes nothing must keep them.
            rig.Peer.MainQueueListed = false;
            rig.Peer.OtherQueues = [new ListedQueue(Support, [AgentMember], [])];
            var refresh = run.Server.ReconcileQueuesAsync().AsTask();
            var timer = await barrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
            barrierClock.Advance(timer.DueTime);
            await refresh.WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                run.Server.Queues.GetByName(Sales)!.Members.Should().ContainKey(AgentMember, "a refresh cut short removes no membership");
                run.Server.Queues.GetByName(Support)!.Members.Should().ContainKey(AgentMember, "it admits the listed one");
                rig.State(Agent).Should().Be(AgentState.Available, "the admitted unpaused membership is settled");
                rig.Changes(Agent).Should().Be(1, "exactly one state-changed notification");
                rig.AssertNoAgentsListingAndCountEqualsTable();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldStillSettleTheAdmittedMembership_WhenAMemberAddedSubscriberThrows()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]));
        var failure = new InvalidOperationException("member-added subscriber");
        // It throws for the new queue's row, after that row was committed; the rows before it were applied too.
        rig.Run.Server.Queues.MemberAdded += (queue, _) =>
        {
            if (queue == Support)
                throw failure;
        };

        rig.Peer.OtherQueues =
        [
            new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember]),
            new ListedQueue(Support, [AgentMember], []),
        ];
        var outcome = await Record.ExceptionAsync(() => rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeSameAs(failure, "the subscriber's failure still leaves the call, as before");
            rig.Run.Server.Queues.GetByName(Support)!.Members.Should().ContainKey(AgentMember, "the row was committed before the drain");
            rig.State(Agent).Should().Be(AgentState.Available, "the committed membership is settled although the drain threw");
            rig.Changes(Agent).Should().Be(1);
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldStillSettleTheRemovedMembership_WhenACallerLeftSubscriberThrowsInTheCompletion()
    {
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember, "PJSIP/2001"], [], PausedMembers: [AgentMember]),
            mainCallers: [HeldCaller]);
        var failure = new InvalidOperationException("caller-left subscriber");
        rig.Run.Server.Queues.CallerLeft += (_, _) => throw failure;

        // The completion drops the held caller (CallerLeft, which throws) and the paused membership, in one transition.
        rig.Peer.QueueEntries = [];
        rig.Peer.OtherQueues = [new ListedQueue(Sales, ["PJSIP/2001"], [])];
        var outcome = await Record.ExceptionAsync(() => rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().BeSameAs(failure, "the subscriber's failure still leaves the call, as before");
            rig.Run.Server.Queues.GetByName(Sales)!.Members.Should().NotContainKey(AgentMember, "the completion committed the removal");
            rig.State(Agent).Should().Be(AgentState.Available, "the removal is settled although the completion's drain threw");
            rig.Changes(Agent).Should().Be(1);
            rig.AssertCountEqualsTable();
        }
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldSettleEveryAgent_WhenAnAgentStateChangedSubscriberThrows()
    {
        await using var rig = await AgentRig.StartAsync(
            [new ListedQueue(Sales, [AgentMember, OtherAgentMember], [], PausedMembers: [AgentMember, OtherAgentMember])],
            agents: [Agent, OtherAgent]);
        rig.State(Agent).Should().Be(AgentState.Paused);
        rig.State(OtherAgent).Should().Be(AgentState.Paused);
        rig.Run.Server.Agents.AgentStateChanged += _ => throw new InvalidOperationException("agent subscriber");

        rig.Peer.OtherQueues = [new ListedQueue(Sales, [], [])];
        var outcome = await Record.ExceptionAsync(() => rig.Run.Server.ReconcileQueuesAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            outcome.Should().NotBeNull("an agent subscriber's failure leaves the call, as from a live member event");
            rig.State(Agent).Should().Be(AgentState.Available, "every agent whose membership was removed is settled");
            rig.State(OtherAgent).Should().Be(AgentState.Available, "the failure of one agent's handler does not skip the other");
            rig.Changes(Agent).Should().Be(1);
            rig.Changes(OtherAgent).Should().Be(1);
            rig.AssertNoAgentsListingAndCountEqualsTable();
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldRaiseNoAgentChange_WhenAReloadReadsAPausedMembership()
    {
        // The boundary of the refresh's settle: a load's own agents step announces the agent, with no state change.
        await using var rig = await AgentRig.StartAsync(new ListedQueue(Sales, [AgentMember], []));
        rig.Peer.OtherQueues = [new ListedQueue(Sales, [AgentMember], [], PausedMembers: [AgentMember])];

        await rig.Run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            rig.State(Agent).Should().Be(AgentState.Paused, "the reload's agents step derives the paused agent");
            rig.Changes(Agent).Should().Be(0, "and a load raises no state-changed notification for it");
        }
    }

    /// <summary>A started run whose peer lists the given queues and logged-in agents, counting agent changes after the load.</summary>
    private sealed class AgentRig : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, int> _changes = new(StringComparer.Ordinal);
        private int _agentsAsked;

        private AgentRig(BootingAsterisk peer, Run run)
        {
            Peer = peer;
            Run = run;
        }

        public BootingAsterisk Peer { get; }

        public Run Run { get; }

        public static Task<AgentRig> StartAsync(params ListedQueue[] queues) => StartAsync(queues, [Agent]);

        public static Task<AgentRig> StartAsync(ListedQueue queue, IReadOnlyList<QueueCaller> mainCallers) =>
            StartAsync([queue], [Agent], mainCallers);

        public static async Task<AgentRig> StartAsync(IReadOnlyList<ListedQueue> queues, IReadOnlyList<string> agents,
            IReadOnlyList<QueueCaller>? mainCallers = null)
        {
            var peer = new BootingAsterisk
            {
                BootedAtLogin = true,
                OtherQueues = queues,
                QueueEntries = mainCallers ?? [],
                Agents = [.. agents.Select(id => new ListedAgent(id))],
            };
            var run = await Run.StartAsync(peer);
            var rig = new AgentRig(peer, run) { _agentsAsked = peer.Asked("Agents") };
            run.Server.Agents.AgentStateChanged += agent => rig._changes.AddOrUpdate(agent.AgentId, 1, static (_, n) => n + 1);
            return rig;
        }

        public AgentState? State(string agentId) => Run.Server.Agents.GetById(agentId)?.State;

        public int Changes(string agentId) => _changes.GetValueOrDefault(agentId);

        public void AssertNoAgentsListingAndCountEqualsTable()
        {
            Peer.Asked("Agents").Should().Be(_agentsAsked, "the refresh asks no agents listing");
            AssertCountEqualsTable();
        }

        public void AssertCountEqualsTable() => HeldCallerRecord.AssertEqualsTable(Run.Server.Queues);

        public ValueTask DisposeAsync() => Run.DisposeAsync();
    }
}

/// <summary>The held-Uniqueid count of a <see cref="QueueManager"/> read against its queue table.</summary>
internal static class HeldCallerRecord
{
    /// <summary>
    /// Asserts that for every Uniqueid the count holds exactly the number of entries, across all queues, that carry it,
    /// and that it holds no Uniqueid no entry carries.
    /// </summary>
    public static void AssertEqualsTable(QueueManager queues)
    {
        var table = queues.Queues
            .SelectMany(q => q.Entries.Values)
            .Where(e => e.UniqueId is not null)
            .GroupBy(e => e.UniqueId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        queues.HeldUniqueIdCounts().Should().BeEquivalentTo(table, "the held-caller record equals the queue table");
    }
}

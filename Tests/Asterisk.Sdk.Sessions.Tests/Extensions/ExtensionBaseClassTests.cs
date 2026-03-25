using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests.Extensions;

public sealed class ExtensionBaseClassTests
{
    [Fact]
    public async Task CallRouterBase_CanRouteAsync_ShouldReturnTrueByDefault()
    {
        var router = new TestCallRouter();
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);

        var result = await router.CanRouteAsync(session, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CallRouterBase_SelectNodeForOriginateAsync_ShouldDelegateToSelectNode()
    {
        var router = new TestCallRouter();

        var result = await router.SelectNodeForOriginateAsync("sales", "5551234", null, CancellationToken.None);

        result.Should().Be("test-node");
    }

    [Fact]
    public async Task CallRouterBase_SelectNodeForOriginateAsync_ShouldPassMetadata()
    {
        var router = new TestCallRouter();
        var metadata = new Dictionary<string, string> { ["key1"] = "val1" };

        var result = await router.SelectNodeForOriginateAsync(null, null, metadata, CancellationToken.None);

        result.Should().Be("test-node");
    }

    [Fact]
    public async Task SessionStoreBase_GetActiveAsync_ShouldReturnEmptyByDefault()
    {
        var store = new TestSessionStore();

        var result = await store.GetActiveAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionStoreBase_DeleteAsync_ShouldCompleteWithoutError()
    {
        var store = new TestSessionStore();

        var act = async () => await store.DeleteAsync("s1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SessionStoreBase_GetByLinkedIdAsync_ShouldReturnNullByDefault()
    {
        var store = new TestSessionStore();

        var result = await store.GetByLinkedIdAsync("linked-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SessionStoreBase_SaveBatchAsync_ShouldCallSaveForEachSession()
    {
        var store = new TestSessionStore();
        var sessions = new List<CallSession>
        {
            new("s1", "l1", "srv1", CallDirection.Inbound),
            new("s2", "l2", "srv1", CallDirection.Outbound)
        };

        await store.SaveBatchAsync(sessions, CancellationToken.None);

        store.SavedSessions.Should().HaveCount(2);
    }

    [Fact]
    public async Task AgentSelectorBase_RankAgentsAsync_ShouldReturnCandidatesUnchanged()
    {
        var selector = new TestAgentSelector();
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        var queue = new AsteriskQueue { Name = "sales" };
        var candidates = new List<AsteriskAgent>
        {
            new() { AgentId = "a1" },
            new() { AgentId = "a2" }
        };

        var result = await selector.RankAgentsAsync(queue, session, candidates, CancellationToken.None);

        result.Should().BeSameAs(candidates);
    }

    // --- Test doubles ---

    private sealed class TestCallRouter : CallRouterBase
    {
        public override ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct)
            => ValueTask.FromResult("test-node");
    }

    private sealed class TestSessionStore : SessionStoreBase
    {
        public List<CallSession> SavedSessions { get; } = [];

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            SavedSessions.Add(session);
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<CallSession?>(null);
    }

    private sealed class TestAgentSelector : AgentSelectorBase
    {
        public override ValueTask<AsteriskAgent?> SelectAgentAsync(
            AsteriskQueue queue, CallSession session, CancellationToken ct)
            => ValueTask.FromResult<AsteriskAgent?>(null);
    }
}

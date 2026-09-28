using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class InMemorySessionStoreTests
{
    private readonly InMemorySessionStore _sut = new();

    [Fact]
    public async Task SaveAsync_ShouldPersistSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(session, CancellationToken.None);

        var result = await _sut.GetAsync("s1", CancellationToken.None);
        result.Should().NotBeNull();
        result!.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetAsync("nonexistent", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(session, CancellationToken.None);
        await _sut.DeleteAsync("s1", CancellationToken.None);

        var result = await _sut.GetAsync("s1", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnNonCompleted()
    {
        var active = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        var completed = new CallSession("s2", "l2", "srv1", CallDirection.Inbound);
        completed.TryTransition(CallSessionState.Failed);

        await _sut.SaveAsync(active, CancellationToken.None);
        await _sut.SaveAsync(completed, CancellationToken.None);

        var result = await _sut.GetActiveAsync(CancellationToken.None);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task OnReleasedByManager_ShouldRemoveTheSession_WhenTheStoreHoldsThatSameObject()
    {
        var released = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(released, CancellationToken.None);

        _sut.OnReleasedByManager(released);

        var byId = await _sut.GetAsync("s1", CancellationToken.None);
        var byLinkedId = await _sut.GetByLinkedIdAsync("l1", CancellationToken.None);
        new { ById = byId?.SessionId, ByLinkedId = byLinkedId?.SessionId }.Should().BeEquivalentTo(
            new { ById = (string?)null, ByLinkedId = (string?)null },
            "the default store keeps the manager's own object, so it lets go of it when the manager does");
    }

    [Fact]
    public async Task OnReleasedByManager_ShouldKeepTheSession_WhenTheStoreHoldsADifferentObjectUnderItsId()
    {
        var released = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        var newer = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(released, CancellationToken.None);
        await _sut.SaveAsync(newer, CancellationToken.None);

        _sut.OnReleasedByManager(released);

        var held = await _sut.GetAsync("s1", CancellationToken.None);
        var what = held is null ? "nothing" : ReferenceEquals(held, newer) ? "the newer session" : "the released session";
        what.Should().Be("the newer session",
            "the release names one session object; a different session saved under the same id since "
            + "is not removed on the released one's account");
    }
}

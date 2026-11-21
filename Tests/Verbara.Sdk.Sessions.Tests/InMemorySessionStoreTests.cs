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
            new { ById = default(string), ByLinkedId = default(string) },
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

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnNothing_WhenTwoServersHoldTheLinkedId()
    {
        await _sut.SaveAsync(new CallSession("session-a", "shared", "server-a", CallDirection.Inbound), CancellationToken.None);
        await _sut.SaveAsync(new CallSession("session-b", "shared", "server-b", CallDirection.Inbound), CancellationToken.None);

        var found = await _sut.GetByLinkedIdAsync("shared", CancellationToken.None);

        found.Should().BeNull("two servers hold the linkedid, so a lookup that names no server cannot pick one of them");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnTheNamedServersSession_WhenTwoServersHoldTheLinkedId()
    {
        await _sut.SaveAsync(new CallSession("session-a", "shared", "server-a", CallDirection.Inbound), CancellationToken.None);
        await _sut.SaveAsync(new CallSession("session-b", "shared", "server-b", CallDirection.Inbound), CancellationToken.None);

        var a = await _sut.GetByLinkedIdAsync("server-a", "shared", CancellationToken.None);
        var b = await _sut.GetByLinkedIdAsync("server-b", "shared", CancellationToken.None);
        var c = await _sut.GetByLinkedIdAsync("server-c", "shared", CancellationToken.None);

        new { A = a?.SessionId, B = b?.SessionId, C = c?.SessionId }.Should().BeEquivalentTo(
            new { A = "session-a", B = "session-b", C = default(string) },
            "a lookup that names a server returns that server's session, and nothing for a server holding none");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnTheNewestSession_WhenOneServerHoldsTheLinkedIdTwice()
    {
        var now = DateTimeOffset.UtcNow;
        await _sut.SaveAsync(
            new CallSession("newer", "shared", "server-a", CallDirection.Inbound) { CreatedAt = now }, CancellationToken.None);
        await _sut.SaveAsync(
            new CallSession("older", "shared", "server-a", CallDirection.Inbound) { CreatedAt = now.AddMinutes(-1) },
            CancellationToken.None);

        var unscoped = await _sut.GetByLinkedIdAsync("shared", CancellationToken.None);
        var scoped = await _sut.GetByLinkedIdAsync("server-a", "shared", CancellationToken.None);

        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "newer", Scoped = "newer" },
            "one server holds the linkedid, so both lookups answer with its newest session");
    }

    [Fact]
    public async Task DeleteAsync_ShouldLeaveTheOtherServersSessionFoundByLinkedId_WhenTwoServersShareOneLinkedId()
    {
        await _sut.SaveAsync(new CallSession("session-a", "shared", "server-a", CallDirection.Inbound), CancellationToken.None);
        await _sut.SaveAsync(new CallSession("session-b", "shared", "server-b", CallDirection.Inbound), CancellationToken.None);

        await _sut.DeleteAsync("session-a", CancellationToken.None);

        var unscoped = await _sut.GetByLinkedIdAsync("shared", CancellationToken.None);
        var scoped = await _sut.GetByLinkedIdAsync("server-b", "shared", CancellationToken.None);
        new { Unscoped = unscoped?.SessionId, Scoped = scoped?.SessionId }.Should().BeEquivalentTo(
            new { Unscoped = "session-b", Scoped = "session-b" },
            "server-b still holds the linkedid, and it is now the only server that does");
    }
}

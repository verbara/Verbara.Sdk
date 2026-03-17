using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

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
}

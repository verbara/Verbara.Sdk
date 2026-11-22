using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class ISessionStoreTests
{
    [Fact]
    public void InMemorySessionStore_ShouldImplementISessionStore_WhenInspectedViaReflection()
    {
        typeof(ISessionStore).IsAssignableFrom(typeof(InMemorySessionStore)).Should().BeTrue();
    }

    [Fact]
    public void SessionStoreBase_ShouldImplementISessionStore_WhenInspectedViaReflection()
    {
        typeof(ISessionStore).IsAssignableFrom(typeof(SessionStoreBase)).Should().BeTrue();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2012:Use ValueTasks correctly",
        Justification = "NSubstitute intercepts the ValueTask return on the mock to configure behavior; it is not consumed as an awaitable here.")]
    public async Task ISessionStore_ShouldBeMockable_WhenUsedWithNSubstitute()
    {
        var expected = new CallSession("s-mock", "l-mock", "srv-mock", CallDirection.Inbound);
        var mock = Substitute.For<ISessionStore>();
        mock.GetAsync("s-mock", Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<CallSession?>(expected));

        var result = await mock.GetAsync("s-mock", CancellationToken.None);

        result.Should().NotBeNull();
        result!.SessionId.Should().Be("s-mock");
    }

    [Fact]
    public async Task UseInMemory_ShouldRegisterInMemorySessionStore_WhenCalledOnBuilder()
    {
        var services = new ServiceCollection();
        var builder = new TestSessionsBuilder(services);

        builder.UseInMemory();

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<ISessionStore>();

        store.Should().NotBeNull();
        store.Should().BeAssignableTo<ISessionStore>();

        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await store.SaveAsync(session, CancellationToken.None);

        var roundTripped = await store.GetAsync("s1", CancellationToken.None);
        roundTripped.Should().NotBeNull();
        roundTripped!.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task GetByLinkedIdAsync_ShouldReturnNothing_WhenTheUnscopedAnswerBelongsToAnotherServer()
    {
        ISessionStore store = new DirectSessionStore(new CallSession("session-b", "shared", "server-b", CallDirection.Inbound));

        var named = await store.GetByLinkedIdAsync("server-b", "shared", CancellationToken.None);
        var other = await store.GetByLinkedIdAsync("server-a", "shared", CancellationToken.None);

        new { Named = named?.SessionId, Other = other?.SessionId }.Should().BeEquivalentTo(
            new { Named = "session-b", Other = default(string) },
            "the interface's default scoped lookup keeps the unscoped answer only when it belongs to the named server");
    }

    /// <summary>A store that implements the interface directly, so the scoped lookup is the interface default.</summary>
    private sealed class DirectSessionStore(CallSession answer) : ISessionStore
    {
        public ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<CallSession?>(null);

        public ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
            => ValueTask.FromResult(Enumerable.Empty<CallSession>());

        public ValueTask DeleteAsync(string sessionId, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
            => ValueTask.FromResult<CallSession?>(answer.LinkedId == linkedId ? answer : null);

        public ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal test-only <see cref="ISessionsBuilder"/> implementation so we can exercise
    /// the fluent builder without pulling in the full <c>AddVerbaraSessionsBuilder</c>
    /// wiring from <c>Verbara.Sdk.Hosting</c> (which would register hosted services
    /// unrelated to this unit test).
    /// </summary>
    private sealed class TestSessionsBuilder : ISessionsBuilder
    {
        public TestSessionsBuilder(IServiceCollection services)
        {
            Services = services;
        }

        public IServiceCollection Services { get; }
    }
}

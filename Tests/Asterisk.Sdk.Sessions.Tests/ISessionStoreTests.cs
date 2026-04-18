using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests;

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

    /// <summary>
    /// Minimal test-only <see cref="ISessionsBuilder"/> implementation so we can exercise
    /// the fluent builder without pulling in the full <c>AddAsteriskSessionsBuilder</c>
    /// wiring from <c>Asterisk.Sdk.Hosting</c> (which would register hosted services
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

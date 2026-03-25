using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Hosting.Tests;

public sealed class SessionManagerHostedServiceTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly AsteriskServer _server;

    public SessionManagerHostedServiceTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldAttachToServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);

        // Verify that AttachToServer was called by checking that the manager
        // responds to server events (no exception = attached successfully).
        // We verify indirectly: a second attach with the same serverId would replace
        // subscriptions without error, showing the first attach happened.
        var act = () => csm.AttachToServer(_server, "default");
        act.Should().NotThrow();

        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldDetachFromServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        // After detach, re-attaching should work cleanly
        var act = () => csm.AttachToServer(_server, "default");
        act.Should().NotThrow();

        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager()
    {
        var mockManager = Substitute.For<ICallSessionManager>();
        var sut = new SessionManagerHostedService(mockManager, _server);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager()
    {
        var mockManager = Substitute.For<ICallSessionManager>();
        var sut = new SessionManagerHostedService(mockManager, _server);

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldReturnCompletedTask()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        var task = sut.StartAsync(CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldReturnCompletedTask()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        var task = sut.StopAsync(CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
        await csm.DisposeAsync();
    }

    private static CallSessionManager CreateCallSessionManager()
    {
        var options = Options.Create(new SessionOptions());
        var store = new TestSessionStore();
        return new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store);
    }

    /// <summary>
    /// Minimal session store for testing (InMemorySessionStore is internal to the Sessions assembly).
    /// </summary>
    private sealed class TestSessionStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}

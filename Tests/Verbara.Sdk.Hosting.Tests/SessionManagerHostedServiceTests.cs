using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SessionManagerHostedServiceTests : IAsyncLifetime
{
    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _server;

    public SessionManagerHostedServiceTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldAttachToServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        // Ends on the attach's own signal: a channel the server reports opens a session. Calling
        // AttachToServer again and expecting no throw proved nothing — a second attach under the same id
        // replaces the subscription set without detaching the first, so it is green with no attach at all.
        csm.ActiveSessions.Should().ContainSingle("the started service attached the manager to the server")
            .Which.LinkedId.Should().Be("linked-1");

        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldDetachFromServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        await sut.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Ring, linkedId: "linked-2");

        // Ends on the detach's own signal: the manager was attached (the first channel opened a session),
        // and a channel the server reports after the stop opens nothing.
        csm.ActiveSessions.Select(s => s.LinkedId).Should().Equal(["linked-1"],
            "the start attached the manager and the stop detached it");

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

    // --- Attach and detach only (spec: session-persistence-lifecycle) ---
    //
    // The token every save runs under is SessionPersistenceTokenService's (SessionPersistenceTokenServiceTests); this
    // service attaches the manager to the single server and detaches it, and hands no token.

    [Fact]
    public async Task StartAsync_ShouldHandNoToken_WhenDrivenWithoutTheTokenService()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateCallSessionManager(store, new RecordingLogger());
        using var sut = new SessionManagerHostedService(manager, _server);

        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        store.Saves.Should().ContainSingle("the start attached the manager, so the channel opened a session")
            .Which.Token.CanBeCanceled.Should().BeFalse(
                "the attach service hands no token: one service owns the token the manager saves under, so no two "
                + "services write it");
    }

    [Fact]
    public async Task StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(stop.Token);

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        store.Saves.Should().BeEmpty(
            "the stop detached the manager from the server, so a channel event after the stop starts no " +
            "save at all");
        manager.GetByLinkedId("linked-1").Should().BeNull("the manager is no longer subscribed to the server");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Dispose_ShouldNotThrow_WhenCalledMoreThanOnce()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateCallSessionManager(store, new RecordingLogger());
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);

        var disposable = sut.Should().BeAssignableTo<IDisposable>().Subject;
        disposable.Dispose();

        // IDisposable's contract: a second Dispose must be ignored, not throw. The container disposes once,
        // but a type whose Dispose is not idempotent is a landmine for anyone who wraps it.
        var second = () => disposable.Dispose();
        second.Should().NotThrow();
    }

    [Fact]
    public async Task StartAsync_ShouldThrowAndAttachNothing_WhenTheServiceWasDisposed()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateCallSessionManager(store, new RecordingLogger());
        var sut = new SessionManagerHostedService(manager, _server);
        sut.Dispose();

        var error = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        error.Should().BeOfType<ObjectDisposedException>("a disposed service cannot be started again");
        manager.GetByLinkedId("linked-1").Should().BeNull("the refused start attached the manager to nothing");
        store.Saves.Should().BeEmpty("no session was opened, so nothing was saved");
    }

    private static CallSessionManager CreateCallSessionManager()
    {
        var options = Options.Create(new SessionOptions());
        var store = new TestSessionStore();
        return new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store);
    }

    private static CallSessionManager CreateCallSessionManager(
        SessionStoreBase store,
        ILogger<CallSessionManager> logger) =>
        new(Options.Create(new SessionOptions()), logger, store);

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

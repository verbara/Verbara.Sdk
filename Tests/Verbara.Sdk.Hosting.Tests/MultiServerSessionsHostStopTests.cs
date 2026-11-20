using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host built with a multi-server sessions registration ends its saves with the stop phase, as a single-server host
/// does, and attaches no server for the host: attaching the servers of a pool stays the host's. Every fact builds a
/// real host through the public registration, so it measures what a consumer's host does, not what a hand-made pair of
/// services does. Every wait ends on a signal: the pending store ends a save on the cancelling thread.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class MultiServerSessionsHostStopTests : IAsyncLifetime
{
    /// <summary>The type name of the one owner of the persistence token, pinned before the type exists.</summary>
    private const string TokenOwnerTypeName = "SessionPersistenceTokenService";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _poolServer = NewServer();
    private readonly VerbaraServer _diServer = NewServer();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _poolServer.DisposeAsync();
        await _diServer.DisposeAsync();
    }

    public enum Registrations
    {
        MultiServer,
        MultiServerBuilder,
        SingleThenMultiServer,
        MultiThenSingleServer,
        MultiServerTwice,
    }

    [Theory]
    [InlineData(Registrations.MultiServer)]
    [InlineData(Registrations.MultiServerBuilder)]
    public async Task HostStopAsync_ShouldCutShortAnInFlightSave_WhenAMultiServerHostStopsNoLongerGraceful(
        Registrations registrations)
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        var host = BuildHost(store, logger, registrations);
        // Disposed asynchronously: the manager the container owns is IAsyncDisposable only.
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store);

        // Already cancelled, so the host passes its hosted services a cancelled stop token: the shutdown is no
        // longer graceful when it reaches them. Never a timeout, which fires on a timer.
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await host.StopAsync(withdrawn.Token);

        save.Token.CanBeCanceled.Should().BeTrue(
            "the multi-server registration hands the manager a token from a source the stop phase can cancel");
        save.IsObserved.Should().BeTrue(
            "the shutdown was no longer graceful, so the save was cut short instead of outliving the host");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(
            e => e.Level >= LogLevel.Error,
            "a save the shutdown cut short is not a persistence failure");
    }

    [Theory]
    [InlineData(Registrations.MultiServer)]
    [InlineData(Registrations.MultiServerBuilder)]
    public async Task HostStopAsync_ShouldLeaveAnInFlightSaveRunning_WhenAMultiServerHostStopsGracefully(
        Registrations registrations)
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        var host = BuildHost(store, logger, registrations);
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store);
        using var stop = new CancellationTokenSource();

        await host.StopAsync(stop.Token);
        var afterStop = save.IsObserved;
        await ((IAsyncDisposable)host).DisposeAsync();

        afterStop.Should().BeFalse(
            "a graceful stop is exactly the case in which the session state is still worth persisting");
        save.IsObserved.Should().BeTrue(
            "the host's teardown cancels the persistence token before releasing it, so the save ends rather than "
            + "being left under a token nothing can ever cancel");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(Registrations.MultiServer)]
    [InlineData(Registrations.MultiServerBuilder)]
    [InlineData(Registrations.MultiServerTwice)]
    public async Task HostStartAsync_ShouldAttachNoServer_WhenAMultiServerHostHoldsADiServer(Registrations registrations)
    {
        var store = new PendingSaveStore();
        var host = BuildHost(store, new RecordingLogger(), registrations, holdDiServer: true);
        // Disposed asynchronously: the manager the container owns is IAsyncDisposable only.
        await using var disposal = (IAsyncDisposable)host;
        var manager = (CallSessionManager)host.Services.GetRequiredService<ICallSessionManager>();
        await host.StartAsync(CancellationToken.None);

        _diServer.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        manager.ActiveSessions.Should().BeEmpty(
            "the multi-server registration attaches no server: a DI server is the host's to attach, or not");
        store.Saves.Should().BeEmpty("no session was opened, so nothing was saved");
        await host.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(Registrations.SingleThenMultiServer)]
    [InlineData(Registrations.MultiThenSingleServer)]
    [InlineData(Registrations.MultiServerTwice)]
    public async Task AddVerbaraSessionsMultiServer_ShouldRegisterOneTokenOwner_WhenRegisteredMoreThanOnce(
        Registrations registrations)
    {
        var store = new PendingSaveStore();
        var host = BuildHost(store, new RecordingLogger(), registrations, holdDiServer: true);
        // Disposed asynchronously: the manager the container owns is IAsyncDisposable only.
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store);

        var owners = host.Services.GetServices<IHostedService>().Count(s => s.GetType().Name == TokenOwnerTypeName);
        await host.StopAsync(CancellationToken.None);

        save.Token.CanBeCanceled.Should().BeTrue("the host's saves run under a token the stop phase can cancel");
        owners.Should().Be(1,
            "however many sessions registrations a host makes, one service owns the persistence token, so no two "
            + "services write the token the manager saves under");
    }

    // --- No new save after the stop, across services and under both stop modes (ADR-0059 R4) ---
    //
    // The cancellation of an already-cancelled stop token runs inline, and a server event raised from it is the only
    // moment at which the order "every detach, then the cancel" is observable. The event is raised from the
    // persistence token's own cancellation callback, so no clock decides when it arrives.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStopAsync_ShouldNotStartASave_WhenThePoolHostDetachesInItsOwnStopAndAnEventArrivesInline(
        bool stopConcurrently)
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        // Registered before the sessions call, so a concurrent stop reaches it after the token service: it opens the
        // detacher's gate only then, which makes the detach finish after every other service's stop has been entered.
        var opener = new GateOpeningHostedService();
        if (!stopConcurrently)
            opener.Open();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ServicesStopConcurrently = stopConcurrently);
                services.AddSingleton<SessionStoreBase>(store);
                services.AddSingleton<ILogger<CallSessionManager>>(logger);
                services.AddSingleton<IHostedService>(opener);
                services.AddVerbaraSessionsMultiServer(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
                services.AddSingleton<IHostedService>(sp => new PoolAttachingHostedService(
                    (CallSessionManager)sp.GetRequiredService<ICallSessionManager>(), _poolServer, opener.Opened));
            })
            .Build();
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        _poolServer.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle("the host attached its pool server in its own start").Subject;
        save.Token.Register(() => _poolServer.Channels.OnNewChannel(
            "uid-2", "PJSIP/100-002", ChannelState.Ring, linkedId: "linked-2"));
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await host.StopAsync(withdrawn.Token);

        save.Token.IsCancellationRequested.Should().BeTrue(
            "the withdrawn grace cancelled the persistence token, so the callback above really ran");
        store.Saves.Should().ContainSingle(
            "the host's stop token is wired onto the persistence source only after every hosted service's stop has "
            + "returned, so the host's own detach precedes the cancellation and no second save is started");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStopAsync_ShouldNotStartASave_WhenTheSingleServerHostStopsAndAnEventArrivesInline(
        bool stopConcurrently)
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ServicesStopConcurrently = stopConcurrently);
                services.AddSingleton<SessionStoreBase>(store);
                services.AddSingleton<ILogger<CallSessionManager>>(logger);
                services.AddSingleton(_diServer);
                services.AddVerbaraSessions(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
            })
            .Build();
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        _diServer.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle("the SDK attached the single server at start").Subject;
        save.Token.Register(() => _diServer.Channels.OnNewChannel(
            "uid-2", "PJSIP/100-002", ChannelState.Ring, linkedId: "linked-2"));
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await host.StopAsync(withdrawn.Token);

        save.Token.IsCancellationRequested.Should().BeTrue(
            "the withdrawn grace cancelled the persistence token, so the callback above really ran");
        store.Saves.Should().ContainSingle(
            "the SDK's detach precedes the cancellation, so the event reaches a detached manager");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStartAsync_ShouldSaveUnderTheStopPhaseToken_WhenAServerAnnouncesACallInAnEarlierServicesStart(
        bool multiServer)
    {
        var store = new PendingSaveStore();
        var raiser = new StartRaisingHostedService(multiServer ? _poolServer : _diServer);
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SessionStoreBase>(store);
                services.AddSingleton<ILogger<CallSessionManager>>(new RecordingLogger());
                services.AddSingleton(_diServer);
                services.AddSingleton<IHostedService>(raiser);
                if (multiServer)
                {
                    services.AddVerbaraSessionsMultiServer(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
                    services.AddSingleton<IHostedService>(sp => new StartingAttacher(
                        (CallSessionManager)sp.GetRequiredService<ICallSessionManager>(), _poolServer));
                }
                else
                {
                    services.AddVerbaraSessions(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
                }
            })
            .Build();
        await using var disposal = (IAsyncDisposable)host;

        await host.StartAsync(CancellationToken.None);

        var save = store.Saves.Should().ContainSingle().Subject;
        save.Token.CanBeCanceled.Should().BeTrue("the token is handed in StartingAsync, before any StartAsync");
        await host.StopAsync(CancellationToken.None);
    }

    private sealed class StartRaisingHostedService(VerbaraServer server) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            server.Channels.OnNewChannel("uid-s", "PJSIP/300-001", ChannelState.Ring, linkedId: "linked-s");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StartingAttacher(CallSessionManager manager, VerbaraServer server) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            manager.AttachToServer(server, "node-1");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            manager.DetachFromServer("node-1");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Attaches the pool server the way a consumer does, raises one channel and returns its one save.</summary>
    private PendingSave TakeOneSave(IHost host, PendingSaveStore store)
    {
        var manager = (CallSessionManager)host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_poolServer, "node-1");
        _poolServer.Channels.OnNewChannel("uid-9", "PJSIP/200-009", ChannelState.Ring, linkedId: "linked-9");
        return store.Saves.Should().ContainSingle("the attached server's channel opened one session").Subject;
    }

    private IHost BuildHost(
        PendingSaveStore store,
        RecordingLogger logger,
        Registrations registrations,
        bool holdDiServer = false) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SessionStoreBase>(store);
                services.AddSingleton<ILogger<CallSessionManager>>(logger);
                if (holdDiServer || registrations is Registrations.SingleThenMultiServer or Registrations.MultiThenSingleServer)
                    services.AddSingleton(_diServer);

                Action<SessionOptions> off = o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan;
                switch (registrations)
                {
                    case Registrations.MultiServer:
                        services.AddVerbaraSessionsMultiServer(off);
                        break;
                    case Registrations.MultiServerBuilder:
                        services.AddVerbaraSessionsMultiServerBuilder(off);
                        break;
                    case Registrations.SingleThenMultiServer:
                        services.AddVerbaraSessions(off);
                        services.AddVerbaraSessionsMultiServer(off);
                        break;
                    case Registrations.MultiThenSingleServer:
                        services.AddVerbaraSessionsMultiServer(off);
                        services.AddVerbaraSessions(off);
                        break;
                    case Registrations.MultiServerTwice:
                        services.AddVerbaraSessionsMultiServer(off);
                        services.AddVerbaraSessionsMultiServer(off);
                        break;
                }
            })
            .Build();

    private static VerbaraServer NewServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    /// <summary>Opens a gate in its own stop, or before it when the test opens it itself.</summary>
    private sealed class GateOpeningHostedService : IHostedService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _gate.Task;

        public void Open() => _gate.TrySetResult();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A consumer's pool host: attaches its server in its own start and detaches it in its own stop, once the gate is
    /// open, as a host whose detach takes a while.
    /// </summary>
    private sealed class PoolAttachingHostedService(CallSessionManager manager, VerbaraServer server, Task gate)
        : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            manager.AttachToServer(server, "node-1");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await gate.ConfigureAwait(false);
            manager.DetachFromServer("node-1");
        }
    }
}

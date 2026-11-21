using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host that puts its own <see cref="ICallSessionManager"/> in front of the SDK's engine, built through the public
/// sessions registrations as a consumer writes them. A decorator over an engine the host registers as
/// <see cref="CallSessionManager"/> keeps what an undecorated host gets — the attach of the single DI server, the
/// stop-phase persistence token, a teardown that does not throw — and a manager behind which no engine is reachable is
/// told so once at start, while the SDK builds no engine of its own to reach.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class DecoratedSessionManagerHostTests : IAsyncLifetime
{
    /// <summary>The category the persistence token service logs under.</summary>
    private const string TokenServiceCategory = "Verbara.Sdk.Hosting.SessionPersistenceTokenService";

    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _server = NewServer();

    public enum Shape
    {
        /// <summary>No decorator: the SDK's default registration.</summary>
        Undecorated,

        /// <summary>The engine as <see cref="CallSessionManager"/>, a decorator over it as the interface, after the sessions call.</summary>
        DecoratorAfter,

        /// <summary>The same two registrations made before the sessions call (its TryAdd then skips the interface).</summary>
        DecoratorBefore,

        /// <summary>The engine as <see cref="CallSessionManager"/>, the interface forwarded to that same instance.</summary>
        ForwardedInterface,

        /// <summary>An engine registered as <see cref="CallSessionManager"/> beside the SDK's default interface: two engines.</summary>
        EngineBeside,

        /// <summary>The interface replaced by a decorator over an engine built with <c>new</c>, unknown to the container.</summary>
        DecoratorOverNew,

        /// <summary>The interface replaced by a decorator over an engine built with ActivatorUtilities, unknown to the container.</summary>
        DecoratorOverActivator,

        /// <summary>The interface replaced by a manager of the host's own that wraps no engine.</summary>
        Foreign,

        /// <summary>The interface replaced by a test substitute.</summary>
        Substitute,
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    public static TheoryData<Shape> DecoratedSingleServerShapes() =>
        [Shape.DecoratorAfter, Shape.DecoratorBefore, Shape.DecoratorOverNew, Shape.DecoratorOverActivator];

    public static TheoryData<Shape, bool> RegisteredEngineDecorators()
    {
        var data = new TheoryData<Shape, bool>();
        foreach (var shape in new[] { Shape.DecoratorAfter, Shape.DecoratorBefore })
        {
            data.Add(shape, false);
            data.Add(shape, true);
        }

        return data;
    }

    public static TheoryData<Shape, bool> UnreachableEngineShapes()
    {
        var data = new TheoryData<Shape, bool>();
        foreach (var shape in new[] { Shape.DecoratorOverNew, Shape.DecoratorOverActivator, Shape.Foreign, Shape.Substitute })
        {
            data.Add(shape, false);
            data.Add(shape, true);
        }

        return data;
    }

    public static TheoryData<Shape, bool> ReachableEngineShapes()
    {
        var data = new TheoryData<Shape, bool>();
        foreach (var shape in new[]
            { Shape.Undecorated, Shape.DecoratorAfter, Shape.DecoratorBefore, Shape.ForwardedInterface, Shape.EngineBeside })
        {
            data.Add(shape, false);
            data.Add(shape, true);
        }

        return data;
    }

    // --- R1: the single-server attach reaches a decorated manager ---

    [Theory]
    [MemberData(nameof(DecoratedSingleServerShapes))]
    public async Task HostStartAsync_ShouldOpenASession_WhenTheSingleServerManagerIsDecorated(Shape shape)
    {
        var store = new PendingSaveStore();
        var host = Build(store, shape, multiServer: false);
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var saves = store.Saves.Count;
        var active = manager.ActiveSessions.Select(s => s.LinkedId).ToArray();
        await host.StopAsync(CancellationToken.None);

        active.Should().Equal(["linked-1"], "the single-server registration attaches the DI server through the decorator");
        saves.Should().Be(1, "the engine behind the decorator saves the session it opened");
    }

    // --- R2: the stop detaches through the decorator ---

    [Fact]
    public async Task HostStopAsync_ShouldDetachThroughTheDecorator_WhenTheSingleServerManagerIsDecorated()
    {
        var store = new PendingSaveStore();
        ForwardingManager? decorator = null;
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                AddCommon(services, store);
                services.AddVerbaraSessions(SweepOff);
                services.AddSingleton<CallSessionManager>();
                services.AddSingleton<ICallSessionManager>(sp =>
                    decorator = new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: false));
            })
            .Build();
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        await host.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Ring, linkedId: "linked-2");

        decorator!.Calls.Should().Equal(["attach:default", "detach:default"],
            "the SDK attaches at start and detaches at stop through the registered manager");
        manager.ActiveSessions.Select(s => s.LinkedId).Should().Equal(["linked-1"],
            "a channel raised after the stop opens no session");
    }

    // --- R3: the persistence token reaches the engine behind a decorator over a registered engine ---

    [Theory]
    [MemberData(nameof(RegisteredEngineDecorators))]
    public async Task HostStartAsync_ShouldRunTheSaveUnderACancellableToken_WhenTheManagerIsADecoratorOverARegisteredEngine(
        Shape shape, bool multiServer)
    {
        var store = new PendingSaveStore();
        var host = Build(store, shape, multiServer);
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store, multiServer);
        await host.StopAsync(CancellationToken.None);

        save.Token.CanBeCanceled.Should().BeTrue("the save runs under the stop-phase token the SDK handed the engine");
    }

    [Theory]
    [MemberData(nameof(RegisteredEngineDecorators))]
    public async Task HostStopAsync_ShouldCutShortAnInFlightSave_WhenADecoratedHostStopsNoLongerGraceful(
        Shape shape, bool multiServer)
    {
        var store = new PendingSaveStore();
        var host = Build(store, shape, multiServer);
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store, multiServer);
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await host.StopAsync(withdrawn.Token);

        save.IsObserved.Should().BeTrue("a non-graceful stop cuts the in-flight save short");
        save.Failure.Should().BeOfType<OperationCanceledException>();
    }

    [Theory]
    [MemberData(nameof(RegisteredEngineDecorators))]
    public async Task HostDisposeAsync_ShouldEndAnInFlightSave_WhenADecoratedHostIsTornDown(Shape shape, bool multiServer)
    {
        var store = new PendingSaveStore();
        var host = Build(store, shape, multiServer);
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store, multiServer);
        await host.StopAsync(CancellationToken.None);
        var runningAfterStop = !save.IsObserved;

        await ((IAsyncDisposable)host).DisposeAsync();

        runningAfterStop.Should().BeTrue("a graceful stop leaves the in-flight save running");
        save.IsObserved.Should().BeTrue("the teardown cancels the persistence token");
    }

    // --- R4: one Warning when no engine is reachable ---

    [Theory]
    [MemberData(nameof(UnreachableEngineShapes))]
    public async Task HostStartAsync_ShouldWarnOnce_WhenNoEngineIsReachable(Shape shape, bool multiServer)
    {
        var capture = new CategoryLogCapture();
        var host = Build(new PendingSaveStore(), shape, multiServer, capture);
        string? managerType;
        await using ((IAsyncDisposable)host)
        {
            managerType = host.Services.GetRequiredService<ICallSessionManager>().GetType().FullName;
            await host.StartAsync(CancellationToken.None);
            // A second start of the same token service instance, as a caller that also drives it as a plain
            // hosted service would make: still one Warning.
            await TokenService(host.Services).StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        }

        var warning = capture.Entries.Where(e => e.Level >= LogLevel.Warning).Should()
            .ContainSingle("one Warning per start says the token reached no engine, and no other service says it")
            .Subject;
        warning.Should().Match<CategorizedLogEntry>(e =>
            e.Category == TokenServiceCategory && e.Level == LogLevel.Warning && e.EventId.Id == 1);
        warning.Values.Should().Contain("ManagerType", managerType,
            "the Warning names the manager the host resolves, the one thing in it a host can act on");
    }

    [Fact]
    public async Task HostStartAsync_ShouldWarnOnce_WhenAHostWithTwoSessionsRegistrationsReachesNoEngine()
    {
        var capture = new CategoryLogCapture();
        var host = new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(capture))
            .ConfigureServices(services =>
            {
                AddCommon(services, new PendingSaveStore());
                services.AddVerbaraSessions(SweepOff);
                services.AddVerbaraSessionsMultiServer(SweepOff);
                services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(new ForeignManager()));
            })
            .Build();
        await using ((IAsyncDisposable)host)
        {
            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        }

        var warning = capture.Entries.Where(e => e.Level >= LogLevel.Warning).Should()
            .ContainSingle("one Warning per start says the token reached no engine, and no other service says it")
            .Subject;
        warning.Should().Match<CategorizedLogEntry>(e =>
            e.Category == TokenServiceCategory && e.Level == LogLevel.Warning && e.EventId.Id == 1);
        warning.Values.Should().Contain("ManagerType", typeof(ForeignManager).FullName,
            "the Warning names the manager the host resolves");
    }

    // --- C1: no Warning when an engine is reachable ---

    [Theory]
    [MemberData(nameof(ReachableEngineShapes))]
    public async Task HostStartAsync_ShouldNotWarn_WhenAnEngineIsReachable(Shape shape, bool multiServer)
    {
        var capture = new CategoryLogCapture();
        var host = Build(new PendingSaveStore(), shape, multiServer, capture);
        await using ((IAsyncDisposable)host)
        {
            await host.StartAsync(CancellationToken.None);
            await TokenService(host.Services).StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        }

        capture.Entries.Where(e => e.Category == TokenServiceCategory && e.Level >= LogLevel.Warning)
            .Should().BeEmpty("the token reached an engine, so there is nothing to warn about");
    }

    // --- C2: the SDK builds no engine of its own ---

    [Theory]
    [InlineData(Shape.DecoratorOverNew)]
    [InlineData(Shape.DecoratorOverActivator)]
    [InlineData(Shape.Foreign)]
    public async Task HostStartAsync_ShouldBuildNoSecondEngine_WhenTheManagerIsReplaced(Shape shape)
    {
        var store = new PendingSaveStore();
        var host = Build(store, shape, multiServer: false);
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var manager = host.Services.GetRequiredService<ICallSessionManager>();

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var saves = store.Saves.Count;
        var active = manager.ActiveSessions.Count();
        var registeredEngine = host.Services.GetService<CallSessionManager>();
        await host.StopAsync(CancellationToken.None);

        registeredEngine.Should().BeNull("the SDK registers no CallSessionManager the host did not register");
        saves.Should().Be(active,
            "only the manager the host resolves opens sessions and saves; no hidden engine is attached to the server");
    }

    // --- C3: an engine registered beside the default interface ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStartAsync_ShouldHandTheTokenToTheInterfacesEngine_WhenAnotherEngineIsRegisteredBesideIt(bool multiServer)
    {
        var store = new PendingSaveStore();
        var host = Build(store, Shape.EngineBeside, multiServer);
        await using var disposal = (IAsyncDisposable)host;
        await host.StartAsync(CancellationToken.None);
        var save = TakeOneSave(host, store, multiServer);
        await host.StopAsync(CancellationToken.None);

        host.Services.GetRequiredService<ICallSessionManager>().Should()
            .NotBeSameAs(host.Services.GetRequiredService<CallSessionManager>(), "premise: the container holds two engines");
        save.Token.CanBeCanceled.Should().BeTrue(
            "the token goes to the engine the interface resolves, which is the one attached, as before this change");
    }

    // --- R6: the teardown with a manager disposed twice ---

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HostDisposeAsync_ShouldNotThrow_WhenADecoratorForwardsTheDisposal(bool multiServer, bool forwardedInterface)
    {
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                AddCommon(services, new PendingSaveStore());
                if (multiServer)
                    services.AddVerbaraSessionsMultiServer(SweepOff);
                else
                    services.AddVerbaraSessions(SweepOff);
                services.AddSingleton<CallSessionManager>();
                if (forwardedInterface)
                    services.AddSingleton<ICallSessionManager>(sp => sp.GetRequiredService<CallSessionManager>());
                else
                    services.AddSingleton<ICallSessionManager>(sp =>
                        new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: true));
            })
            .Build();
        await host.StartAsync(CancellationToken.None);
        _ = host.Services.GetRequiredService<ICallSessionManager>();
        await host.StopAsync(CancellationToken.None);

        var act = async () => await ((IAsyncDisposable)host).DisposeAsync();

        await act.Should().NotThrowAsync("the container and the forwarding registration may both dispose the engine");
    }

    // --- R7: a host that also attaches by hand behaves as an undecorated one ---

    [Fact]
    public async Task HostStartAsync_ShouldOpenTwoSessionsPerCall_WhenADecoratedHostAlsoAttachesTheDiServerUnderAnotherId()
    {
        var (threw, saves, ids) = await RunHandAttach(_server, "manual");

        threw.Should().BeNull();
        saves.Should().Be(2, "as on an undecorated host, the SDK's attach and the hand attach each open a session");
        ids.Should().Equal(["default", "manual"]);
    }

    [Fact]
    public async Task HostStartAsync_ShouldOpenOneSession_WhenADecoratedHostAlsoAttachesTheDiServerUnderDefault()
    {
        var (threw, saves, ids) = await RunHandAttach(_server, "default");

        threw.Should().BeNull();
        saves.Should().Be(1, "a second attach of the same server under the same id adds nothing");
        ids.Should().Equal(["default"]);
    }

    [Fact]
    public async Task HostStartAsync_ShouldThrow_WhenADecoratedHostAlsoAttachesAnotherServerUnderDefault()
    {
        await using var other = NewServer();

        var (threw, saves, ids) = await RunHandAttach(other, "default");

        threw.Should().BeOfType<InvalidOperationException>(
            "as on an undecorated host, \"default\" already holds the DI server when the hand attach runs");
        saves.Should().Be(1);
        ids.Should().Equal(["default"]);
    }

    private async Task<(Exception? Threw, int Saves, string[] Ids)> RunHandAttach(VerbaraServer handServer, string handId)
    {
        var store = new PendingSaveStore();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                AddCommon(services, store);
                services.AddVerbaraSessions(SweepOff);
                services.AddSingleton<CallSessionManager>();
                services.AddSingleton<ICallSessionManager>(sp =>
                    new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: false));
                services.AddSingleton<IHostedService>(sp =>
                    new HandAttach(sp.GetRequiredService<ICallSessionManager>(), handServer, handId));
            })
            .Build();
        await using var disposal = (IAsyncDisposable)host;
        var threw = await Record.ExceptionAsync(() => host.StartAsync(CancellationToken.None));
        var manager = host.Services.GetRequiredService<ICallSessionManager>();

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var ids = manager.ActiveSessions.Select(s => s.ServerId).Order(StringComparer.Ordinal).ToArray();
        var saves = store.Saves.Count;
        await host.StopAsync(CancellationToken.None);
        return (threw, saves, ids);
    }

    /// <summary>A host's own hosted service that attaches a server by hand through the resolved manager.</summary>
    private sealed class HandAttach(ICallSessionManager manager, VerbaraServer server, string id) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            manager.AttachToServer(server, id);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            manager.DetachFromServer(id);
            return Task.CompletedTask;
        }
    }

    // --- helpers ---

    private static void SweepOff(SessionOptions options) => options.ReconciliationInterval = Timeout.InfiniteTimeSpan;

    private void AddCommon(IServiceCollection services, PendingSaveStore store)
    {
        services.AddSingleton<SessionStoreBase>(store);
        services.AddSingleton<ILogger<CallSessionManager>>(new RecordingLogger());
        services.AddSingleton(_server);
    }

    private static IHostedService TokenService(IServiceProvider services) =>
        services.GetServices<IHostedService>().Single(s => s.GetType().Name == "SessionPersistenceTokenService");

    /// <summary>
    /// Opens one session and returns its save. Single-server: through the SDK's attach if it made one, else through a
    /// hand attach of the resolved manager under <c>"default"</c>, so the token fact is measured whatever the attach
    /// does. Multi-server: a hand attach through the resolved manager, as a pool host makes it.
    /// </summary>
    private PendingSave TakeOneSave(IHost host, PendingSaveStore store, bool multiServer)
    {
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        if (multiServer)
        {
            manager.AttachToServer(_server, "node-1");
        }
        else
        {
            _server.Channels.OnNewChannel("uid-0", "PJSIP/100-000", ChannelState.Ring, linkedId: "linked-0");
            if (store.Saves.Count == 0)
                manager.AttachToServer(_server, "default");
        }

        _server.Channels.OnNewChannel("uid-9", "PJSIP/200-009", ChannelState.Ring, linkedId: "linked-9");
        return store.Saves.Should().NotBeEmpty().And.Subject.Last();
    }

    private IHost Build(PendingSaveStore store, Shape shape, bool multiServer, CategoryLogCapture? capture = null) =>
        new HostBuilder()
            .ConfigureLogging(logging =>
            {
                if (capture is not null)
                    logging.AddProvider(capture);
            })
            .ConfigureServices(services =>
            {
                AddCommon(services, store);
                if (shape == Shape.DecoratorBefore)
                    AddEngineAndDecorator(services);

                if (multiServer)
                    services.AddVerbaraSessionsMultiServer(SweepOff);
                else
                    services.AddVerbaraSessions(SweepOff);

                switch (shape)
                {
                    case Shape.DecoratorAfter:
                        AddEngineAndDecorator(services);
                        break;
                    case Shape.ForwardedInterface:
                        services.AddSingleton<CallSessionManager>();
                        services.AddSingleton<ICallSessionManager>(sp => sp.GetRequiredService<CallSessionManager>());
                        break;
                    case Shape.EngineBeside:
                        services.AddSingleton<CallSessionManager>();
                        break;
                    case Shape.DecoratorOverNew:
                        services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                            new ForwardingManager(
                                new CallSessionManager(
                                    sp.GetRequiredService<IOptions<SessionOptions>>(),
                                    sp.GetRequiredService<ILogger<CallSessionManager>>(),
                                    sp.GetRequiredService<SessionStoreBase>()),
                                ownsInner: true)));
                        break;
                    case Shape.DecoratorOverActivator:
                        services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                            new ForwardingManager(ActivatorUtilities.CreateInstance<CallSessionManager>(sp), ownsInner: true)));
                        break;
                    case Shape.Foreign:
                        services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(new ForeignManager()));
                        break;
                    case Shape.Substitute:
                        services.Replace(ServiceDescriptor.Singleton(Substitute.For<ICallSessionManager>()));
                        break;
                }
            })
            .Build();

    private static void AddEngineAndDecorator(IServiceCollection services)
    {
        services.AddSingleton<CallSessionManager>();
        services.AddSingleton<ICallSessionManager>(sp =>
            new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: false));
    }

    private static VerbaraServer NewServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }
}

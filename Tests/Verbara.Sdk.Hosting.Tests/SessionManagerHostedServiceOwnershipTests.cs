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
/// Who the single-server sessions registration releases at stop. Built through the public registration as a host writes
/// it: the stop releases the <c>"default"</c> attachment only when the SDK's own start installed it, and a host's
/// attachment made before start, or one the start never replaced, survives the stop.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SessionManagerHostedServiceOwnershipTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _server = NewServer();
    private readonly VerbaraServer _other = NewServer();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync().AsTask().WaitAsync(Bound);
        await _other.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    // --- 1.1: an undecorated host's hand attach made before start ---

    [Fact]
    public async Task StopAsync_ShouldKeepTheHostsAttach_WhenTheHostAttachedDefaultBeforeStart()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        var inFlight = OpenCall(_server, "uid-1", "linked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");
        _server.Channels.OnHangup("uid-1");

        manager.GetByLinkedId("linked-2").Should().NotBeNull(
            "the host attached \"default\" before start, so the SDK's stop leaves that attachment in place");
        inFlight.State.Should().Be(CallSessionState.Failed, "the call in flight still hears its hangup after the stop");
    }

    // --- 1.2: a decorated host's hand attach made before start ---

    [Fact]
    public async Task StopAsync_ShouldNotDetachThroughTheDecorator_WhenTheHostAttachedDefaultBeforeStart()
    {
        ForwardingManager? decorator = null;
        var host = BuildHost(decorated: true, onDecorator: d => decorator = d);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        var inFlight = OpenCall(_server, "uid-1", "linked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");
        _server.Channels.OnHangup("uid-1");

        decorator!.Calls.Should().Equal(["attach:default", "attach:default"],
            "the decorator sees the host's attach and the SDK's attach, and no detach from the SDK");
        manager.GetByLinkedId("linked-2").Should().NotBeNull("the host's attachment survives the SDK's stop");
        inFlight.State.Should().Be(CallSessionState.Failed, "the call in flight still hears its hangup after the stop");
    }

    // --- 1.3: the SDK's attach threw, because another server holds "default" ---

    [Fact]
    public async Task StopAsync_ShouldKeepTheOtherServersAttach_WhenTheStartsAttachThrew()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_other, "default");

        var threw = await Record.ExceptionAsync(() => host.StartAsync(CancellationToken.None).WaitAsync(Bound));
        var inFlight = OpenCall(_other, "o-1", "olinked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _other.Channels.OnNewChannel("o-2", "PJSIP/900-002", ChannelState.Up, linkedId: "olinked-2");
        _other.Channels.OnHangup("o-1");

        threw.Should().BeOfType<InvalidOperationException>("\"default\" already held another server when the SDK attached");
        manager.GetByLinkedId("olinked-2").Should().NotBeNull(
            "the start installed nothing, so the stop leaves the other server's attachment in place");
        inFlight.State.Should().Be(CallSessionState.Failed, "the other server's call in flight still hears its hangup");
    }

    // --- 1.4: the SDK's attach never ran, because an earlier service failed the start ---

    [Fact]
    public async Task StopAsync_ShouldDetachNothing_WhenAnEarlierServiceFailedTheStart()
    {
        var host = BuildHost(decorated: false, earlierThrowingService: true);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");

        var threw = await Record.ExceptionAsync(() => host.StartAsync(CancellationToken.None).WaitAsync(Bound));
        var inFlight = OpenCall(_server, "uid-1", "linked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");
        _server.Channels.OnHangup("uid-1");

        threw.Should().BeOfType<InvalidOperationException>("the earlier service failed the start before the SDK attached");
        manager.GetByLinkedId("linked-2").Should().NotBeNull(
            "the SDK's attach never ran, so its stop leaves the host's attachment in place");
        inFlight.State.Should().Be(CallSessionState.Failed, "the call in flight still hears its hangup after the stop");
    }

    // --- 1.5 controls ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ShouldDetachDefault_WhenNothingWasAttachedByHand(bool decorated)
    {
        ForwardingManager? decorator = null;
        var host = BuildHost(decorated, onDecorator: d => decorator = d);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        OpenCall(_server, "uid-1", "linked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        manager.GetByLinkedId("linked-2").Should().BeNull("the SDK installed the attachment, so its stop releases it");
        if (decorated)
            decorator!.Calls.Should().Equal(["attach:default", "detach:default"]);
    }

    [Fact]
    public async Task StopAsync_ShouldDetachDefault_WhenTheHostAttachedAfterStart()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        manager.AttachToServer(_server, "default");
        OpenCall(_server, "uid-1", "linked-1", manager);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        manager.GetByLinkedId("linked-2").Should().BeNull(
            "the SDK attached first and owns the installation; the host's later attach of the same server added nothing");
    }

    [Fact]
    public async Task StopAsync_ShouldDetachDefault_WhenTheHostAttachedTheServerUnderAnotherIdBeforeStart()
    {
        ForwardingManager? decorator = null;
        var host = BuildHost(decorated: true, onDecorator: d => decorator = d);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "mine");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        decorator!.Calls.Should().Equal(["attach:mine", "attach:default", "detach:default"],
            "the host held this server under another id, not under \"default\", so the SDK installed \"default\" and its "
            + "stop releases it");
        manager.GetByLinkedId("linked-2").Should().NotBeNull("the host's attachment under its own id survives the stop");
    }

    [Fact]
    public async Task StartAsync_ShouldAttachAndStopShouldDetachTheOwnManager_WhenNoEngineIsRegistered()
    {
        ForwardingManager? own = null;
        var host = BuildOwnManagerHost(registerEngine: false, onOwn: o => own = o);
        await using var disposal = (IAsyncDisposable)host;

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);

        own!.Calls.Should().Equal(["attach:default", "detach:default"],
            "no engine is reachable, so the SDK's attach is its own and its stop releases it");
    }

    [Fact]
    public async Task StopAsync_ShouldDetachTheOwnManager_WhenARegisteredEngineBesideItWasAttachedByHand()
    {
        ForwardingManager? own = null;
        var host = BuildOwnManagerHost(registerEngine: true, onOwn: o => own = o);
        await using var disposal = (IAsyncDisposable)host;
        var engine = host.Services.GetRequiredService<CallSessionManager>();
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        engine.AttachToServer(_server, "default");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        own!.Calls.Should().Equal(["attach:default", "detach:default"],
            "the SDK's attach reached the host's own manager, not the engine the host attached, so the stop releases it");
        manager.GetByLinkedId("linked-2").Should().BeNull("the own manager hears nothing after the SDK's stop");
        engine.GetByLinkedId("linked-2").Should().NotBeNull("the engine the host attached by hand keeps its attachment");
    }

    // --- the SDK's own attach, not any attach, decides whether it reached the engine ---

    [Theory]
    [InlineData("another server under another id")]
    [InlineData("this server under another id")]
    [InlineData("another server under default")]
    public async Task StopAsync_ShouldDetachTheOwnManager_WhenAnotherAttachReachesTheHandAttachedEngineDuringTheStart(
        string otherAttach)
    {
        ForwardingManager? own = null;
        CallSessionManager? engine = null;
        Exception? otherAttachFailure = null;
        var host = BuildOwnManagerHost(registerEngine: true, onOwn: o => own = o, attachVia: forward =>
        {
            forward();
            // Another attach lands on the engine the host attached by hand, inside the SDK's attach and on its thread.
            otherAttachFailure = Record.Exception(() =>
            {
                switch (otherAttach)
                {
                    case "another server under another id": engine!.AttachToServer(_other, "x"); break;
                    case "this server under another id": engine!.AttachToServer(_server, "x"); break;
                    default: engine!.AttachToServer(_other, "default"); break;
                }
            });
        });
        await using var disposal = (IAsyncDisposable)host;
        engine = host.Services.GetRequiredService<CallSessionManager>();
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        engine.AttachToServer(_server, "default");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        if (otherAttach == "another server under default")
            otherAttachFailure.Should().BeOfType<InvalidOperationException>("\"default\" already holds this server");
        else
            otherAttachFailure.Should().BeNull();
        own!.Calls.Should().Equal(["attach:default", "detach:default"],
            "the SDK's own attach went to the host's own manager; another attach on the engine does not make it the host's");
        manager.GetByLinkedId("linked-2").Should().BeNull("the own manager hears nothing after the SDK's stop");
        // Server-qualified: in the "this server under another id" row the engine holds the server under two ids.
        engine.GetByLinkedId("default", "linked-2").Should().NotBeNull(
            "the engine the host attached by hand keeps its attachment");
    }

    [Fact]
    public async Task StopAsync_ShouldDetachTheOwnManager_WhenAnotherThreadAttachesDefaultOnTheHandAttachedEngineDuringTheStart()
    {
        ForwardingManager? own = null;
        using var release = new ManualResetEventSlim();
        using var attached = new ManualResetEventSlim();
        var otherThreadAttached = false;
        var host = BuildOwnManagerHost(registerEngine: true, onOwn: o => own = o, attachVia: forward =>
        {
            forward();
            release.Set();
            otherThreadAttached = attached.Wait(Bound);
        });
        await using var disposal = (IAsyncDisposable)host;
        var engine = host.Services.GetRequiredService<CallSessionManager>();
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        engine.AttachToServer(_server, "default");
        // A thread that already ran before the start, as a host's own worker would: it makes the very attach the SDK
        // would make on the engine, during the SDK's attach, but it is not the SDK's call.
        var other = new Thread(() =>
        {
            if (!release.Wait(Bound))
                return;
            engine.AttachToServer(_server, "default");
            attached.Set();
        }) { IsBackground = true };
        other.Start();

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        other.Join(Bound).Should().BeTrue();
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        otherThreadAttached.Should().BeTrue("the other thread attached during the SDK's attach");
        own!.Calls.Should().Equal(["attach:default", "detach:default"],
            "the SDK's own attach went to the host's own manager; the other thread's attach was not the SDK's");
        manager.GetByLinkedId("linked-2").Should().BeNull("the own manager hears nothing after the SDK's stop");
        engine.GetByLinkedId("linked-2").Should().NotBeNull("the engine the host attached by hand keeps its attachment");
    }

    [Fact]
    public async Task StopAsync_ShouldNotDetachThroughTheDecorator_WhenItForwardsTheAttachOnAnotherThreadAndWaits()
    {
        ForwardingManager? decorator = null;
        var forwardedOnAnotherThread = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        // A dedicated thread, started from the call (it inherits the caller's execution context) and joined: unlike a
        // waited Task.Run, it can never be inlined on the caller's thread.
        var host = BuildHost(decorated: true, onDecorator: d => decorator = d, attachVia: forward =>
        {
            var caller = Environment.CurrentManagedThreadId;
            var worker = new Thread(() =>
            {
                forwardedOnAnotherThread.Enqueue(Environment.CurrentManagedThreadId != caller);
                forward();
            }) { IsBackground = true };
            worker.Start();
            worker.Join(Bound).Should().BeTrue("the forwarding thread finished within the bound");
        });
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");

        await host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        forwardedOnAnotherThread.Should().NotBeEmpty().And.OnlyContain(other => other,
            "every forward ran on a thread of its own, not the caller's");
        decorator!.Calls.Should().Equal(["attach:default", "attach:default"],
            "the decorator forwarded the SDK's attach to the engine within the call, so the host's attachment is kept");
        manager.GetByLinkedId("linked-2").Should().NotBeNull("the host's attachment survives the SDK's stop");
    }

    // --- the service driven directly: a start after a stop, and a stop without a start ---

    [Fact]
    public async Task StopAsync_ShouldKeepTheHostsAttach_WhenTheServiceIsStartedAndStoppedTwice()
    {
        ForwardingManager? decorator = null;
        var host = BuildHost(decorated: true, onDecorator: d => decorator = d);
        await using var disposal = (IAsyncDisposable)host;
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        var engine = host.Services.GetRequiredService<CallSessionManager>();
        manager.AttachToServer(_server, "default");
        using var service = new SessionManagerHostedService(manager, _server, engine);

        await service.StartingAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        decorator!.Calls.Should().Equal(["attach:default", "attach:default", "attach:default"],
            "the host's attach and one SDK attach per start, and no detach: neither start installed \"default\"");
        manager.GetByLinkedId("linked-2").Should().NotBeNull("the host's attachment survives both stops");
    }

    [Fact]
    public async Task StartAsync_ShouldInstallAndStopShouldReleaseDefault_WhenTheHostDetachedItsOwnAttachBetweenTwoStarts()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = (CallSessionManager)host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");
        using var service = new SessionManagerHostedService(manager, _server, manager);

        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        manager.DetachFromServer("default");
        await service.StartingAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up, linkedId: "linked-1");
        var openedAfterSecondStart = manager.GetByLinkedId("linked-1");
        await service.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        openedAfterSecondStart.Should().NotBeNull("the second start installed \"default\" after the host released it");
        manager.GetByLinkedId("linked-2").Should().BeNull("the second stop releases what the second start installed");
    }

    [Fact]
    public async Task StopAsync_ShouldKeepTheHostsAttach_WhenTheServiceIsBuiltWithoutAnExplicitEngine()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = (CallSessionManager)host.Services.GetRequiredService<ICallSessionManager>();
        manager.AttachToServer(_server, "default");
        // The two-argument constructor takes the manager itself as the engine when it is one.
        using var service = new SessionManagerHostedService(manager, _server);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        manager.GetByLinkedId("linked-2").Should().NotBeNull(
            "the manager is the engine and held \"default\" before the start, so the host's attachment survives the stop");
    }

    [Fact]
    public async Task StopAsync_ShouldDetachNothing_WhenItRunsAgainWithoutAStart()
    {
        var host = BuildHost(decorated: false);
        await using var disposal = (IAsyncDisposable)host;
        var manager = (CallSessionManager)host.Services.GetRequiredService<ICallSessionManager>();
        using var service = new SessionManagerHostedService(manager, _server, manager);

        await service.StartingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        manager.AttachToServer(_server, "default");
        await service.StopAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-2", "PJSIP/100-002", ChannelState.Up, linkedId: "linked-2");

        manager.GetByLinkedId("linked-2").Should().NotBeNull(
            "no start ran before the second stop, so it installed nothing and the host's later attach stays");
    }

    // --- helpers ---

    private static CallSession OpenCall(VerbaraServer server, string uniqueId, string linkedId, ICallSessionManager manager)
    {
        server.Channels.OnNewChannel(uniqueId, $"PJSIP/{uniqueId}", ChannelState.Up, linkedId: linkedId);
        var session = manager.GetByLinkedId(linkedId);
        session.Should().NotBeNull("the call in flight opened a session before the stop");
        return session!;
    }

    private static void SweepOff(SessionOptions options) => options.ReconciliationInterval = Timeout.InfiniteTimeSpan;

    private IHost BuildHost(
        bool decorated,
        Action<ForwardingManager>? onDecorator = null,
        bool earlierThrowingService = false,
        Action<Action>? attachVia = null) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_server);
                services.AddSingleton<ILogger<CallSessionManager>>(NullLogger<CallSessionManager>.Instance);
                if (earlierThrowingService)
                    services.AddSingleton<IHostedService, ThrowingStartingService>();
                services.AddVerbaraSessions(SweepOff);
                if (decorated)
                {
                    services.AddSingleton<CallSessionManager>();
                    services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                    {
                        var decorator = new ForwardingManager(
                            sp.GetRequiredService<CallSessionManager>(), ownsInner: false, attachVia);
                        onDecorator?.Invoke(decorator);
                        return decorator;
                    }));
                }
            })
            .Build();

    /// <summary>
    /// A host whose <see cref="ICallSessionManager"/> is its own manager: a recorder over an engine built with
    /// <c>new</c>, which the container does not know; with or without a <see cref="CallSessionManager"/> the host
    /// registered as itself beside it.
    /// </summary>
    private IHost BuildOwnManagerHost(bool registerEngine, Action<ForwardingManager> onOwn, Action<Action>? attachVia = null) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_server);
                services.AddSingleton<ILogger<CallSessionManager>>(NullLogger<CallSessionManager>.Instance);
                services.AddVerbaraSessions(SweepOff);
                if (registerEngine)
                    services.AddSingleton<CallSessionManager>();
                services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                {
                    var ownEngine = new CallSessionManager(
                        sp.GetRequiredService<IOptions<SessionOptions>>(),
                        NullLogger<CallSessionManager>.Instance,
                        sp.GetRequiredService<SessionStoreBase>());
                    var own = new ForwardingManager(ownEngine, ownsInner: true, attachVia);
                    onOwn(own);
                    return own;
                }));
            })
            .Build();

    private static VerbaraServer NewServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    /// <summary>A hosted service registered before the sessions registration whose start fails the host's start.</summary>
    private sealed class ThrowingStartingService : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("an earlier service failed to start");

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

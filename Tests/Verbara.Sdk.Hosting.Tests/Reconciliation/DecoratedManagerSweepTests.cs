using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The reconciliation sweep of a host that puts its own <see cref="ICallSessionManager"/> in front of the SDK's engine,
/// built through the public sessions registrations. Behind a decorator over an engine the host registers as
/// <see cref="CallSessionManager"/>, the single-server and pool sweeps read that engine and decide as an undecorated
/// host's; behind a decorator over an engine the container does not know they keep the custom-manager path.
/// </summary>
[Collection(SweepCounterGroup.Name)]
public sealed class DecoratedManagerSweepTests
{
    public enum Shape
    {
        /// <summary>The engine as <see cref="CallSessionManager"/>, a pass-through decorator over it as the interface.</summary>
        DecoratorOverRegisteredEngine,

        /// <summary>An engine registered as <see cref="CallSessionManager"/> beside the SDK's default interface.</summary>
        EngineBeside,

        /// <summary>The interface replaced by a decorator over an engine built with <c>new</c>, unknown to the container.</summary>
        DecoratorOverUnregisteredEngine,
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_ShouldRequestOneStatus_WhenTheManagerIsDecoratedOverARegisteredEngine(bool singleServer)
    {
        await using var rig = new DecoratedSweepRig(Shape.DecoratorOverRegisteredEngine, singleServer);
        var call = rig.Reconstructed("restored", "L", young: true);

        await rig.SweepOnceAsync();

        rig.Server.StatusRequests.Should().Be(1, "behind a decorator over a registered engine a pending call on an "
            + "incomplete table supplies a Status candidate, as on an undecorated host; " + rig.Describe());
        rig.EndingsOf(call).Should().Be(1);
        call.Metadata.GetValueOrDefault("cause").Should().Be("reload");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_ShouldSweepTheInterfacesEngine_WhenAnotherEngineIsRegisteredBesideIt(bool singleServer)
    {
        await using var rig = new DecoratedSweepRig(Shape.EngineBeside, singleServer);
        var call = rig.Reconstructed("restored", "L", young: true);

        await rig.SweepOnceAsync();

        rig.Server.StatusRequests.Should().Be(1, "the sweep reads the engine the interface resolves, which holds the "
            + "call, as before this change; " + rig.Describe());
        rig.EndingsOf(call).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_ShouldKeepTheCustomManagerPath_WhenTheDecoratedEngineIsUnknownToTheContainer(bool singleServer)
    {
        await using var rig = new DecoratedSweepRig(Shape.DecoratorOverUnregisteredEngine, singleServer);
        var young = rig.Reconstructed("young", "LY", young: true);
        var old = rig.Reconstructed("old", "LO", young: false);

        await rig.SweepOnceAsync();

        rig.Server.StatusRequests.Should().Be(0, "on the custom-manager path a call never qualifies by waiting for a "
            + "table, and the old call holds no channel the table holds; " + rig.Describe());
        rig.EndingsOf(young).Should().Be(0);
        rig.EndingsOf(old).Should().Be(0);
        // The pool's tick moves its clock one interval, past the dialing timeout, so there both calls are old.
        rig.LastUnverifiable.Should().Be(singleServer ? 1 : 2, "the sweep reads the decorator's calls, not an engine of "
            + "the SDK's own: every old call it holds, holding no channel, is counted unverifiable");
    }

    /// <summary>
    /// A sessions host over one <see cref="RigServer"/>, single-server (<c>AddVerbaraSessions</c> over the DI server,
    /// id <c>"default"</c>) or pool (<c>AddVerbaraMultiServer</c> + <c>AddVerbaraSessionsMultiServer</c>, the server
    /// added to the pool as <c>"a"</c>), with the manager registered in one of the <see cref="Shape"/>s. The server is
    /// attached through the resolved manager by hand; the hosted services are not started, except the pool sweep.
    /// </summary>
    private sealed class DecoratedSweepRig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly bool _singleServer;
        private readonly string _serverId;
        private readonly List<SessionDomainEvent> _events = [];
        private readonly IDisposable _subscription;

        public DecoratedSweepRig(Shape shape, bool singleServer)
        {
            _singleServer = singleServer;
            _serverId = singleServer ? "default" : "a";
            Log.Written = Pulse.Fire;
            Server = new RigServer(_serverId, Pulse);
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Log));
            services.AddSingleton<TimeProvider>(Clock);
            void Configure(SessionOptions options) => options.ReconciliationInterval = PoolSweepRig.Interval;
            if (singleServer)
            {
                services.AddSingleton(Server.Server);
                services.AddVerbaraSessions(Configure);
            }
            else
            {
                services.AddVerbaraMultiServer();
                services.AddVerbaraSessionsMultiServer(Configure);
            }

            switch (shape)
            {
                case Shape.DecoratorOverRegisteredEngine:
                    services.AddSingleton<CallSessionManager>();
                    services.AddSingleton<ICallSessionManager>(sp =>
                        new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: false));
                    break;
                case Shape.EngineBeside:
                    services.AddSingleton<CallSessionManager>();
                    break;
                case Shape.DecoratorOverUnregisteredEngine:
                    services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                        new ForwardingManager(
                            new CallSessionManager(
                                sp.GetRequiredService<IOptions<SessionOptions>>(),
                                sp.GetRequiredService<ILogger<CallSessionManager>>(),
                                sp.GetRequiredService<SessionStoreBase>()),
                            ownsInner: true)));
                    break;
            }

            _provider = services.BuildServiceProvider();
            Manager = _provider.GetRequiredService<ICallSessionManager>();
            if (!singleServer)
                _provider.GetRequiredService<VerbaraServerPool>().AddExistingServer(_serverId, Server.Server);
            Manager.AttachToServer(Server.Server, _serverId);
            _subscription = Manager.Events.Subscribe(new Recorder(this));
        }

        public PoolClock Clock { get; } = new();

        public CapturingLoggerProvider Log { get; } = new();

        public Pulse Pulse { get; } = new();

        public RigServer Server { get; }

        /// <summary>The manager the host resolves.</summary>
        public ICallSessionManager Manager { get; }

        /// <summary>A reconstructed call registered through the resolved manager; an old one is aged a day back.</summary>
        public CallSession Reconstructed(string sessionId, string linkedId, bool young)
        {
            var call = new CallSession(sessionId, linkedId, _serverId, CallDirection.Inbound);
            if (!young)
                SweepRig.CreatedAtField.SetValue(call, Clock.GetUtcNow() - TimeSpan.FromDays(1));
            Manager.RegisterReconstructedSession(call).Should().BeTrue("premise: the manager holds the call");
            return call;
        }

        /// <summary>
        /// One pass of the registered sweep: the single-server sweep's own pass, or one tick of the started pool sweep.
        /// </summary>
        public async Task SweepOnceAsync()
        {
            var hosted = _provider.GetServices<IHostedService>().ToArray();
            if (_singleServer)
            {
                var sweep = hosted.OfType<SessionReconciliationService>().Single();
                await sweep.SweepAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);
                return;
            }

            var pool = hosted.OfType<PoolReconciliationService>().Single();
            await pool.StartAsync(CancellationToken.None);
            try
            {
                Clock.Advance(PoolSweepRig.Interval);
                (await Pulse.WaitUntilAsync(() => Ticks > 0, SweepRig.Bound)).Should().BeTrue("the pool sweep ticked once");
            }
            finally
            {
                await pool.StopAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);
            }
        }

        private int Ticks => Log.Entries.Count(e =>
            e.Category.EndsWith(PoolSweepRig.PoolSweepTypeName, StringComparison.Ordinal) && e.Values.ContainsKey("Serverless"));

        public int EndingsOf(CallSession call)
        {
            lock (_events)
                return _events.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId);
        }

        /// <summary>The <c>unverifiable</c> count of the last pass, or <see langword="null"/> before the first.</summary>
        public int? LastUnverifiable => Log.Entries.LastOrDefault(e => e.Values.ContainsKey("Unverifiable"))
            ?.Values["Unverifiable"] as int?;

        public string Describe() =>
            $"Status: {Server.StatusRequests}; warnings and errors: [" + string.Join(" | ",
                Log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Level} {e.Message}")) + "]";

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await _provider.DisposeAsync();
            if (_singleServer)
                await Server.Server.DisposeAsync();
        }

        private sealed class Recorder(DecoratedSweepRig rig) : IObserver<SessionDomainEvent>
        {
            public void OnNext(SessionDomainEvent value)
            {
                lock (rig._events)
                    rig._events.Add(value);
                rig.Pulse.Fire();
            }

            public void OnError(Exception error)
            {
                // The manager's subject never faults; nothing to record.
            }

            public void OnCompleted()
            {
                // Completed when the manager is disposed; nothing to record.
            }
        }
    }
}

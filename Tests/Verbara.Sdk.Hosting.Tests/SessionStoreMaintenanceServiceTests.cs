using System.Threading.Channels;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
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
/// The sessions host's store maintenance: asleep unless the store asks for it, armed before any hosted service starts,
/// handed exactly what the manager holds on every tick, never overlapped, never stopped by a failing run, and silent
/// after the stop. The clock is manual: a tick happens only when a test advances it past the delay the loop parked on.
/// </summary>
public sealed class SessionStoreMaintenanceServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>Bound on every wait for a signal, so a missing run fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>A store that records its arming and every maintenance run, and runs a test's behaviour for each.</summary>
    private sealed class MaintainedStore : SessionStoreBase
    {
        private readonly Channel<IReadOnlyList<CallSession>> _runs = Channel.CreateUnbounded<IReadOnlyList<CallSession>>();
        private int _inFlight;

        public MaintainedStore(TimeSpan? interval) => Interval = interval;

        public TimeSpan? Interval { get; }

        public int ArmCount { get; private set; }

        public int MaxConcurrentRuns { get; private set; }

        public int RunCount { get; private set; }

        /// <summary>Run for each maintenance call after it is recorded.</summary>
        public Func<int, CancellationToken, Task>? Behaviour { get; set; }

        public ChannelReader<IReadOnlyList<CallSession>> Runs => _runs.Reader;

        internal override TimeSpan? MaintenanceInterval => Interval;

        internal override void ArmMaintenance() => ArmCount++;

        internal override async ValueTask MaintainAsync(IReadOnlyList<CallSession> heldActive, CancellationToken ct)
        {
            var concurrent = Interlocked.Increment(ref _inFlight);
            MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, concurrent);
            var run = ++RunCount;
            try
            {
                _runs.Writer.TryWrite(heldActive);
                if (Behaviour is { } behaviour)
                    await behaviour(run, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    private sealed class WarningCounter : ILoggerProvider, ILogger
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _warningIds = new();

        /// <summary>The event id of every Warning logged with an exception, in order.</summary>
        public IReadOnlyCollection<int> WarningIds => _warningIds;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is not null)
            {
                Interlocked.Increment(ref _warnings);
                _warningIds.Enqueue(eventId.Id);
            }
        }

        public void Dispose()
        {
        }
    }

    private static List<CallSession> Held(params string[] ids) =>
        [.. ids.Select(id => new CallSession(id, "L-" + id, "server-1", CallDirection.Inbound))];

    private static ICallSessionManager ManagerHolding(Func<IEnumerable<CallSession>> held)
    {
        var manager = Substitute.For<ICallSessionManager>();
        manager.ActiveSessions.Returns(_ => held());
        return manager;
    }

    private static async Task<IReadOnlyList<CallSession>> NextRunAsync(MaintainedStore store) =>
        await store.Runs.ReadAsync().AsTask().WaitAsync(Bound);

    /// <summary>Waits until the loop parks on its delay, then moves the clock past it.</summary>
    private static async Task TickAsync(FakeTimeProvider time)
    {
        await time.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);
        time.Advance(Interval);
    }

    [Fact]
    public async Task Start_ShouldNeitherArmNorRun_WhenTheStoreHasNoMaintenanceInterval()
    {
        var store = new MaintainedStore(interval: null);
        var time = new FakeTimeProvider();
        using var sut = new SessionStoreMaintenanceService(
            store, ManagerHolding(() => Held("a")), time, NullLoggerFactory.Instance);

        await sut.StartingAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await sut.StopAsync(CancellationToken.None);

        store.ArmCount.Should().Be(0);
        store.RunCount.Should().Be(0);
        time.TimersCreated.TryRead(out _).Should().BeFalse("an asleep service never waits on the clock");
    }

    [Fact]
    public async Task Host_ShouldArmTheStore_BeforeAnyHostedServiceStarts()
    {
        var store = new MaintainedStore(Interval);
        var armedAtStart = new List<int>();
        var probe = new StartProbe(() => armedAtStart.Add(store.ArmCount));
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // Registered first, so its StartAsync is the first a host runs.
                services.AddSingleton<IHostedService>(probe);
                services.AddSingleton<SessionStoreBase>(store);
                services.AddVerbaraSessionsMultiServer(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
            })
            .Build();

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        armedAtStart.Should().Equal([1], "the store is armed in the starting phase, ahead of every service's start");
    }

    private sealed class StartProbe(Action onStart) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task EachRun_ShouldPassExactlyTheSessionsTheManagerHolds()
    {
        var store = new MaintainedStore(Interval);
        var time = new FakeTimeProvider();
        var held = Held("a", "b");
        using var sut = new SessionStoreMaintenanceService(store, ManagerHolding(() => held), time, NullLoggerFactory.Instance);

        await sut.StartingAsync(CancellationToken.None);
        var first = await NextRunAsync(store);
        held = Held("c");
        await TickAsync(time);
        var second = await NextRunAsync(store);
        await sut.StopAsync(CancellationToken.None);

        store.ArmCount.Should().Be(1);
        first.Select(s => s.SessionId).Should().Equal("a", "b");
        second.Select(s => s.SessionId).Should().Equal("c");
    }

    [Fact]
    public async Task ARunThatThrows_ShouldBeLogged_AndTheNextRunShouldGoOut()
    {
        var store = new MaintainedStore(Interval)
        {
            Behaviour = (run, _) => run == 1 ? throw new InvalidOperationException("store unreachable") : Task.CompletedTask,
        };
        var time = new FakeTimeProvider();
        var warnings = new WarningCounter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(warnings));
        using var sut = new SessionStoreMaintenanceService(store, ManagerHolding(() => Held("a")), time, factory);

        await sut.StartAsync(CancellationToken.None);
        await NextRunAsync(store);
        await TickAsync(time);
        await NextRunAsync(store);
        await sut.StopAsync(CancellationToken.None);

        warnings.Warnings.Should().Be(1);
        store.RunCount.Should().Be(2);
    }

    public enum Registrations
    {
        SingleServer,
        MultiServer,
        SingleAndMultiServer,
        MultiServerTwice,
    }

    [Theory]
    [InlineData(Registrations.SingleServer)]
    [InlineData(Registrations.MultiServer)]
    [InlineData(Registrations.SingleAndMultiServer)]
    [InlineData(Registrations.MultiServerTwice)]
    public async Task Registrations_ShouldAddOneMaintenanceService_HoweverManyAreMade(Registrations registrations)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        services.AddSingleton(new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance));
        Action<SessionOptions> off = o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan;
        switch (registrations)
        {
            case Registrations.SingleServer:
                services.AddVerbaraSessions(off);
                break;
            case Registrations.MultiServer:
                services.AddVerbaraSessionsMultiServer(off);
                break;
            case Registrations.SingleAndMultiServer:
                services.AddVerbaraSessions(off);
                services.AddVerbaraSessionsMultiServer(off);
                break;
            case Registrations.MultiServerTwice:
                services.AddVerbaraSessionsMultiServer(off);
                services.AddVerbaraSessionsMultiServer(off);
                break;
        }

        await using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().Count(s => s is SessionStoreMaintenanceService).Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheLoop_SoNoRunGoesOutAfterIt()
    {
        var store = new MaintainedStore(Interval);
        var time = new FakeTimeProvider();
        using var sut = new SessionStoreMaintenanceService(store, ManagerHolding(() => Held("a")), time, NullLoggerFactory.Instance);
        await sut.StartingAsync(CancellationToken.None);
        await NextRunAsync(store);
        await time.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);

        await sut.StopAsync(CancellationToken.None);
        time.Advance(Interval * 10);

        store.RunCount.Should().Be(1);
        store.Runs.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ARunLongerThanTheInterval_ShouldNotBeOverlapped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MaintainedStore(Interval)
        {
            Behaviour = (run, ct) => run == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask,
        };
        var time = new FakeTimeProvider();
        using var sut = new SessionStoreMaintenanceService(store, ManagerHolding(() => Held("a")), time, NullLoggerFactory.Instance);
        await sut.StartingAsync(CancellationToken.None);
        await NextRunAsync(store);

        // Many intervals pass while the first run is still out: no timer is waiting, so nothing fires.
        time.Advance(Interval * 5);
        store.RunCount.Should().Be(1);
        release.SetResult();
        await TickAsync(time);
        await NextRunAsync(store);
        await sut.StopAsync(CancellationToken.None);

        store.MaxConcurrentRuns.Should().Be(1);
        store.RunCount.Should().Be(2);
    }

    /// <summary>The longest wait <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> accepts.</summary>
    private static readonly TimeSpan DelayLimit = TimeSpan.FromMilliseconds(4_294_967_294);

    [Fact]
    public async Task AnIntervalAboveTheDelayLimit_ShouldNeitherThrowNorStopTheLoop()
    {
        // 200 days: a third of it is past what Task.Delay takes, as a store built by hand over an unvalidated
        // ActiveRecordTtl can ask for.
        var store = new MaintainedStore(TimeSpan.FromDays(200));
        var time = new FakeTimeProvider();
        var warnings = new WarningCounter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(warnings));
        using var sut = new SessionStoreMaintenanceService(store, ManagerHolding(() => Held("a")), time, factory);

        await sut.StartingAsync(CancellationToken.None);
        await NextRunAsync(store);
        var wait = await time.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);
        wait.DueTime.Should().BeLessThanOrEqualTo(DelayLimit);
        time.Advance(wait.DueTime);
        await NextRunAsync(store);
        var stop = () => sut.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
        store.RunCount.Should().Be(2);
        warnings.Warnings.Should().Be(0);
    }

    /// <summary>A clock whose first timer cannot be made; every later one is the manual clock's.</summary>
    private sealed class FirstTimerFails(FakeTimeProvider inner) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("no timer")
                : inner.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task AWaitThatThrows_ShouldBeLogged_AndTheNextRunShouldGoOut()
    {
        // Short, because the wait that replaces the failed one is on the system clock.
        var store = new MaintainedStore(TimeSpan.FromMilliseconds(50));
        var fake = new FakeTimeProvider();
        var warnings = new WarningCounter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(warnings));
        using var sut = new SessionStoreMaintenanceService(
            store, ManagerHolding(() => Held("a")), new FirstTimerFails(fake), factory);

        await sut.StartingAsync(CancellationToken.None);
        await NextRunAsync(store);
        await NextRunAsync(store);
        // The loop goes on on the injected clock after the failed wait.
        await fake.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);
        var stop = () => sut.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
        store.RunCount.Should().Be(2);
        warnings.WarningIds.Should().Equal([2], "the failed wait is logged once, as EventId 2");
    }
}

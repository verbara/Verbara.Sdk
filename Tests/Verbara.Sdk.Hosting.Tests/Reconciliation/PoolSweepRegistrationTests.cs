using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// What the multi-server registrations register for the pool sweep, how it is switched off, and how it shares a host
/// with the single-server sweep and with its own lifetime.
/// </summary>
[Collection(SweepCounterGroup.Name)]
public sealed class PoolSweepRegistrationTests
{
    public enum MultiServerRegistration
    {
        AddVerbaraSessionsMultiServer,
        AddVerbaraSessionsMultiServerBuilder,
    }

    [Theory]
    [InlineData(MultiServerRegistration.AddVerbaraSessionsMultiServer)]
    [InlineData(MultiServerRegistration.AddVerbaraSessionsMultiServerBuilder)]
    public async Task Registration_ShouldAddThePoolSweep_WhenEitherMultiServerRegistrationIsUsed(
        MultiServerRegistration registration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraMultiServer();
        if (registration == MultiServerRegistration.AddVerbaraSessionsMultiServer)
            services.AddVerbaraSessionsMultiServer();
        else
            services.AddVerbaraSessionsMultiServerBuilder();
        await using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().Select(s => s.GetType().Name)
            .Should().Contain(PoolSweepRig.PoolSweepTypeName, "both multi-server registrations register the pool sweep");
    }

    [Fact]
    public async Task Registration_ShouldRunOnePoolSweep_WhenTheMultiServerRegistrationIsCalledTwice()
    {
        await using var rig = new PoolSweepRig(multiServerRegistrations: 2);
        var a = rig.AddServer("a");
        a.LostCall(rig.Manager, "lost");
        a.AsteriskLists();
        await rig.StartAsync();

        await rig.TickAsync();

        new { rig.PoolSweeps, a.StatusRequests }.Should().BeEquivalentTo(
            new { PoolSweeps = 1, StatusRequests = 1 },
            $"registering the multi-server sessions twice runs one pool sweep. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task PoolSweep_ShouldStartAndStopAndSendNothing_WhenTheHostRegistersNoPool()
    {
        await using var rig = new PoolSweepRig(pool: false);
        rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep with or without a pool");

        var start = await Record.ExceptionAsync(() => rig.StartAsync());
        rig.Clock.Advance(PoolSweepRig.Interval);
        var stop = await Record.ExceptionAsync(() => rig.PoolSweep!.StopAsync(CancellationToken.None));

        new { start, stop, rig.Clock.TimersCreated }.Should().BeEquivalentTo(
            new { start = default(Exception), stop = default(Exception), TimersCreated = 0 },
            "without a pool there is nothing to walk: the sweep starts no timer and its start and stop complete");
    }

    [Fact]
    public async Task PoolSweep_ShouldStartNoTimerAndSendNothing_WhenTheIntervalIsInfinite()
    {
        await using var rig = new PoolSweepRig(configure: o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
        rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        var a = rig.AddServer("a");
        var call = a.LostCall(rig.Manager, "lost");
        a.AsteriskLists();
        var before = SweepRig.Look(call);

        var start = await Record.ExceptionAsync(() => rig.StartAsync());
        rig.Clock.Advance(PoolSweepRig.Interval);
        rig.Clock.Advance(PoolSweepRig.Interval);

        new { start, rig.Clock.TimersCreated, a.StatusRequests, Look = SweepRig.Look(call) }.Should().BeEquivalentTo(
            new { start = default(Exception), TimersCreated = 0, StatusRequests = 0, Look = before },
            "an infinite interval switches the pool sweep off: no timer, nothing sent, no call changed");
    }

    [Fact]
    public async Task PoolSweep_ShouldStartNoTimer_WhenTheIntervalIsBoundFromConfigurationAsMinusOneMillisecond()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sessions:ReconciliationInterval"] = "-00:00:00.001",
            })
            .Build();
        await using var rig = new PoolSweepRig(configure: o => configuration.GetSection("Sessions").Bind(o));
        rig.PoolSweep.Should().NotBeNull("the multi-server registration registers the pool sweep");
        var a = rig.AddServer("a");
        a.LostCall(rig.Manager, "lost");
        a.AsteriskLists();

        await rig.StartAsync();
        rig.Clock.Advance(PoolSweepRig.Interval);

        new { Bound = rig.Options.ReconciliationInterval, rig.Clock.TimersCreated, a.StatusRequests }
            .Should().BeEquivalentTo(
                new { Bound = Timeout.InfiniteTimeSpan, TimersCreated = 0, StatusRequests = 0 },
                "\"-00:00:00.001\" is Timeout.InfiniteTimeSpan, the value that switches the sweep off");
    }

    [Fact]
    public async Task PoolSweep_ShouldFailItsStartWithArgumentOutOfRange_WhenTheIntervalIsZero()
    {
        await using var rig = new PoolSweepRig(configure: o => o.ReconciliationInterval = TimeSpan.Zero);
        rig.AddServer("a");

        var start = await Record.ExceptionAsync(() => rig.StartAsync());

        start.Should().BeOfType<ArgumentOutOfRangeException>(
            "an interval of zero or less other than infinite fails the start, as the single-server sweep's does");
    }

    [Fact]
    public async Task Sweeps_ShouldAskTheSingleDiServerOnceAndVerifyTheOtherPoolServer_WhenBothRegistrationsAreUsed()
    {
        await using var rig = new PoolSweepRig(singleServer: true);
        var di = rig.DiServer!;
        rig.Pool!.AddExistingServer("default", di.Server);
        var other = rig.AddServer("b");
        await rig.StartAsync();
        // A call the DI server still lists stays a candidate for every sweep that looks at that server, so a second
        // sweep over it would show as a second Status.
        var onDi = di.LostCall(rig.Manager, "on-di");
        di.AsteriskLists("on-di");
        var onOther = other.LostCall(rig.Manager, "on-other");
        other.AsteriskLists();
        var single = rig.HostedServices.OfType<SessionReconciliationService>().Single();

        await rig.TickAsync();
        await single.SweepAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);

        new { DiStatus = di.StatusRequests, OnDi = onDi.State, OnOther = onOther.State, OtherStatus = other.StatusRequests }
            .Should().BeEquivalentTo(
                new
                {
                    DiStatus = 1, OnDi = CallSessionState.Connected,
                    OnOther = CallSessionState.Completed, OtherStatus = 1,
                },
                "the single DI server, also held by the pool, is verified by the single-server sweep only; the other "
                + $"pool server by the pool sweep. Measured: {rig.Describe()}");
    }
}

using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.TestInfrastructure.Stacks;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// The pool's reconciliation sweep against two real Asterisk servers: a host of the consumers' multi-server shape whose
/// server A's AMI user filters out <c>Hangup</c> (<c>nohangup</c>) and server B's does not (<c>full</c>), so every call
/// on A loses its hangup and every call on B ends with its own.
/// </summary>
/// <remarks>
/// Every test waits on a cause, never on elapsed time: a session's ending, a connection's state, or the <c>full</c> user's
/// <c>Hangup</c> of every leg a server created — and ends only once each server has ended every leg it created, so no
/// call of one test reaches the next.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class PoolSweepFunctionalTests(MultiServerTestFixture fixture, ITestOutputHelper output)
    : IClassFixture<MultiServerTestFixture>
{
    private const int Calls = 3;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly string ShortOnA = $"short@{MultiServerFixture.LabContext}";
    private static readonly string TrunkShortOnA = $"trunk-short@{MultiServerFixture.LabContext}";
    private static readonly string LongOnA = $"direct@{MultiServerFixture.LabContext}";
    private const string ShortOnB = "short@from-dut";

    private readonly MultiServerFixture _lab = fixture.Lab;

    [Fact]
    public async Task PoolSweep_ShouldEndTheLostHangupsOnOneServerAsAReload_WhileTheOtherServersCallsEndByTheirOwnHangup()
    {
        await using var guard = await LegsGuard.StartAsync(fixture);
        await using var host = await PoolSweepHost.StartAsync(_lab);
        var a = await host.JoinAsync("a", 'A', MultiServerAmiUsers.NoHangup);
        var b = await host.JoinAsync("b", 'B', MultiServerAmiUsers.Full);

        await PoolSweepHost.OriginateAsync(a, ShortOnA, Calls);
        await PoolSweepHost.OriginateAsync(b, ShortOnB, Calls);
        var ended = await host.WaitUntilAsync(
            () => host.SessionsOf("a").Count == Calls && host.SessionsOf("a").All(PoolSweepHost.HasEnded)
                && host.SessionsOf("b").Count == Calls && host.SessionsOf("b").All(PoolSweepHost.HasEnded),
            Bound);
        await guard.WaitAllLegsEndedAsync();
        output.WriteLine(host.Describe());

        new
        {
            ended,
            Hosted = host.HostedServices.Contains("PoolReconciliationService"),
            AEndedAsAReload = host.SessionsOf("a").Count(host.EndedAsAReload),
            BEndedByTheirHangup = host.SessionsOf("b").Count(host.EndedByItsHangup),
        }.Should().BeEquivalentTo(
            new { ended = true, Hosted = true, AEndedAsAReload = Calls, BEndedByTheirHangup = Calls },
            "A's calls lost their hangup and end through the sweep as a reload, once each; B's end by their own hangup. "
            + host.Describe());
    }

    [Fact]
    public async Task PoolSweep_ShouldEndTheLegsOfACallThatCrossedTheTrunk_OnEachServerByItsOwnRule()
    {
        await using var guard = await LegsGuard.StartAsync(fixture);
        await using var host = await PoolSweepHost.StartAsync(_lab);
        var a = await host.JoinAsync("a", 'A', MultiServerAmiUsers.NoHangup);
        await host.JoinAsync("b", 'B', MultiServerAmiUsers.Full);

        await PoolSweepHost.OriginateAsync(a, TrunkShortOnA, Calls);
        var ended = await host.WaitUntilAsync(
            () => host.SessionsOf("a").Count == Calls && host.SessionsOf("a").All(PoolSweepHost.HasEnded)
                && host.SessionsOf("b").Count == Calls && host.SessionsOf("b").All(PoolSweepHost.HasEnded),
            Bound);
        await guard.WaitAllLegsEndedAsync();
        output.WriteLine(host.Describe());

        new
        {
            ended,
            AEndedAsAReload = host.SessionsOf("a").Count(host.EndedAsAReload),
            BEndedByTheirHangup = host.SessionsOf("b").Count(host.EndedByItsHangup),
        }.Should().BeEquivalentTo(
            new { ended = true, AEndedAsAReload = Calls, BEndedByTheirHangup = Calls },
            "the origin legs on A lost their hangup and end as a reload; the far legs on B end by their own hangup. "
            + host.Describe());
    }

    [Fact]
    public async Task PoolSweep_ShouldVerifyTheOtherServerWhileOneIsCut_AndTheReconnectShouldEndOnlyTheCutServersGoneCalls()
    {
        var proxy = await _lab.StartAmiPathProxyAsync();
        await using var guard = await LegsGuard.StartAsync(fixture);
        await using var host = await PoolSweepHost.StartAsync(_lab);
        var a = await host.JoinAsync("a", 'A', MultiServerAmiUsers.Full, throughProxy: true);
        var b = await host.JoinAsync("b", 'B', MultiServerAmiUsers.NoHangup);

        await PoolSweepHost.OriginateAsync(b, ShortOnB, Calls);
        await PoolSweepHost.OriginateAsync(a, LongOnA, 1);
        await PoolSweepHost.OriginateAsync(a, ShortOnA, Calls);
        (await host.WaitUntilAsync(() => host.SessionsOf("a").Count == Calls + 1, Bound))
            .Should().BeTrue($"A's calls are open before the cut. {host.Describe()}");

        await proxy.CutAsync();
        try
        {
            var down = await host.WaitUntilAsync(() => a.Connection.State != AmiConnectionState.Connected, Bound);
            // While A is cut: its short calls hang up unseen, and B's lost hangups are verified on B's own.
            await guard.WaitLegsEndedAsync('A', ShortOnA, 2 * Calls);
            var bEndedWhileACut = await host.WaitUntilAsync(
                () => ShortsOfB(host).Count == Calls && ShortsOfB(host).All(host.EndedAsAReload), Bound);
            var aStillCut = a.Connection.State != AmiConnectionState.Connected;
            var aOpenDuringCut = host.SessionsOf("a").Count(s => !PoolSweepHost.HasEnded(s));

            await proxy.RestoreAsync();
            var shortsOfA = await host.WaitUntilAsync(
                () => host.SessionsOf("a").Count(host.EndedAsAReload) == Calls, Bound);
            var longStillUp = host.SessionsOf("a").Count(s => !PoolSweepHost.HasEnded(s));
            var longEnded = await host.WaitUntilAsync(() => host.SessionsOf("a").All(PoolSweepHost.HasEnded), Bound);
            await guard.WaitAllLegsEndedAsync();
            output.WriteLine(host.Describe());

            new
            {
                down, bEndedWhileACut, aStillCut, aOpenDuringCut, shortsOfA, longStillUp, longEnded,
                ALongEndedByItsHangup = host.SessionsOf("a").Count(host.EndedByItsHangup),
                DoubleEndings = host.SessionsOf("a").Concat(host.SessionsOf("b")).Count(s => host.EndingsOf(s) > 1),
                FalseEndingsOfTheLongCall = host.SessionsOf("a", "Local/" + LongOnA).Count(s => s.Metadata.GetValueOrDefault("cause") == "reload"),
            }.Should().BeEquivalentTo(
                new
                {
                    down = true, bEndedWhileACut = true, aStillCut = true, aOpenDuringCut = Calls + 1, shortsOfA = true,
                    longStillUp = 1, longEnded = true, ALongEndedByItsHangup = 1, DoubleEndings = 0,
                    FalseEndingsOfTheLongCall = 0,
                },
                "while A's AMI path is cut, the sweep skips A and verifies B; once A is back its reconnect ends the calls "
                + "that hung up unseen, once each, and the call that stayed up ends by its own hangup. " + host.Describe());
        }
        finally
        {
            await proxy.RestoreAsync();
        }
    }

    [Fact]
    public async Task PoolSweep_ShouldVerifyAServerAddedHot_AndLeaveTheHeldSessionsOfAServerRemovedHot()
    {
        await using var guard = await LegsGuard.StartAsync(fixture);
        await using var host = await PoolSweepHost.StartAsync(_lab);
        var a = await host.JoinAsync("a", 'A', MultiServerAmiUsers.NoHangup);
        var b = await host.AddHotAsync("b", 'B', MultiServerAmiUsers.NoHangup);

        await PoolSweepHost.OriginateAsync(b, ShortOnB, Calls);
        var addedEnded = await host.WaitUntilAsync(
            () => host.SessionsOf("b").Count == Calls && host.SessionsOf("b").All(host.EndedAsAReload), Bound);

        await PoolSweepHost.OriginateAsync(a, ShortOnA, Calls);
        (await host.WaitUntilAsync(() => host.SessionsOf("a").Count == Calls, Bound))
            .Should().BeTrue($"A's calls are open before A is removed. {host.Describe()}");
        await host.RemoveAsync("a");
        await guard.WaitLegsEndedAsync('A', ShortOnA, 2 * Calls);

        // The fence: B's next lost hangups end through a tick that runs after A's held calls passed the dialing timeout.
        await PoolSweepHost.OriginateAsync(b, ShortOnB, Calls);
        var laterTick = await host.WaitUntilAsync(
            () => host.SessionsOf("b").Count == 2 * Calls && host.SessionsOf("b").All(host.EndedAsAReload), Bound);
        await guard.WaitAllLegsEndedAsync();
        output.WriteLine(host.Describe());

        new
        {
            addedEnded, laterTick,
            RemovedHeld = host.SessionsOf("a").Count(s => !PoolSweepHost.HasEnded(s)),
        }.Should().BeEquivalentTo(
            new { addedEnded = true, laterTick = true, RemovedHeld = Calls },
            "a server added to the running host is verified by the next tick; the sessions of a server removed from the "
            + "pool are left held, never verified nor ended. " + host.Describe());
    }

    [Fact]
    public async Task PoolSweep_ShouldLeaveTheLostHangupsOpen_WhenTheIntervalIsInfinite()
    {
        await using var guard = await LegsGuard.StartAsync(fixture);
        await using var off = await PoolSweepHost.StartAsync(_lab, sweepOn: false);
        await using var on = await PoolSweepHost.StartAsync(_lab);
        var aOff = await off.JoinAsync("a", 'A', MultiServerAmiUsers.NoHangup);
        await on.JoinAsync("a", 'A', MultiServerAmiUsers.NoHangup);

        await PoolSweepHost.OriginateAsync(aOff, ShortOnA, Calls);

        // The fence: a host with the sweep on, attached to the same server with the same timeouts, has ended them.
        var onEnded = await on.WaitUntilAsync(
            () => on.SessionsOf("a").Count == Calls && on.SessionsOf("a").All(on.EndedAsAReload), Bound);
        await guard.WaitAllLegsEndedAsync();
        output.WriteLine($"off: {off.Describe()} | on: {on.Describe()}");

        new
        {
            onEnded,
            OffHosted = off.HostedServices.Contains("PoolReconciliationService"),
            OffOpen = off.SessionsOf("a").Count(s => !PoolSweepHost.HasEnded(s)),
        }.Should().BeEquivalentTo(
            new { onEnded = true, OffHosted = true, OffOpen = Calls },
            "an infinite interval switches the pool sweep off: the same lost hangups stay open on the host that set it");
    }

    private static IReadOnlyList<CallSession> ShortsOfB(PoolSweepHost host) => host.SessionsOf("b", "Local/" + ShortOnB);

    /// <summary>
    /// The <c>full</c> user on each server, counting every leg the server creates and its <c>Hangup</c>: a test ends
    /// only once each server has ended every leg it created.
    /// </summary>
    private sealed class LegsGuard : IAsyncDisposable
    {
        private readonly Ami.Connection.AmiConnection _a;
        private readonly Ami.Connection.AmiConnection _b;
        private readonly AmiEventTally _talliesA;
        private readonly AmiEventTally _talliesB;

        private LegsGuard(MultiServerTestFixture fixture)
        {
            _a = fixture.Connect('A', MultiServerAmiUsers.Full);
            _b = fixture.Connect('B', MultiServerAmiUsers.Full);
            _talliesA = new AmiEventTally("A:full", _a);
            _talliesB = new AmiEventTally("B:full", _b);
        }

        public static async Task<LegsGuard> StartAsync(MultiServerTestFixture fixture)
        {
            var guard = new LegsGuard(fixture);
            await guard._a.ConnectAsync();
            await guard._b.ConnectAsync();
            return guard;
        }

        /// <summary>Waits until <paramref name="server"/> has ended at least <paramref name="legs"/> legs of calls to <paramref name="target"/>.</summary>
        public Task WaitLegsEndedAsync(char server, string target, int legs) =>
            (server == 'A' ? _talliesA : _talliesB).WaitUntilAsync(
                t => t.Channels("Hangup").Count(c => c.StartsWith("Local/" + target, StringComparison.Ordinal)) >= legs,
                MultiServerTestFixture.CallBound, $"server {server}'s Hangup of {legs} legs of {target}");

        /// <summary>Waits until each server has ended every leg it created since the guard started.</summary>
        public async Task WaitAllLegsEndedAsync()
        {
            foreach (var tally in new[] { _talliesA, _talliesB })
            {
                await tally.WaitUntilAsync(
                    t => t.UniqueIds("Newchannel").IsSubsetOf(t.UniqueIds("Hangup")),
                    MultiServerTestFixture.CallBound, $"{tally.Label}'s Hangup of every leg it created");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _talliesA.Dispose();
            _talliesB.Dispose();
            await _a.DisposeAsync();
            await _b.DisposeAsync();
        }
    }
}

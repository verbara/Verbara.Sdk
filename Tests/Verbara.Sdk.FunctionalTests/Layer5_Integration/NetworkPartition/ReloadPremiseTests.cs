namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using AmiConnection = Verbara.Sdk.Ami.Connection.AmiConnection;

/// <summary>
/// The two Asterisk-side premises the live-state reload rests on, bound against a real Asterisk
/// instead of assumed (openspec change <c>a-reconnect-reload-is-a-diff-not-a-wipe</c>, task 3.3):
/// <list type="number">
/// <item><description>
/// design D4's residual — that a <c>Status</c> response carries <c>Linkedid</c>, so the reload has
/// a correlation identifier to pass through and one surviving call stays one call.
/// </description></item>
/// <item><description>
/// requirement 2's premise — that "the reload is the last notification such a call will ever
/// produce". If Asterisk replayed the <c>Hangup</c> events emitted while the AMI connection was
/// down, the reload would not be the last word and the requirement would have to narrow.
/// </description></item>
/// </list>
/// Both were measured on Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1 on 2026-09-24. These tests
/// run against whichever version the container fixture builds, so a future version that breaks
/// either premise turns one of them red instead of silently invalidating the spec.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ReloadPremiseTests : FunctionalTestBase
{
    private const string ProxyName = ToxiproxyFixture.AmiProxyName;

    // A two-leg call whose outbound halves land in a real bridge: the originated leg runs Dial()
    // towards a second Local leg. The /n suffix keeps Asterisk from optimising the local pair
    // away, so the bridge survives long enough to be observed.
    private const string BridgedChannel = "Local/100@test-functional/n";
    private const string BridgedDialData = "Local/100@test-functional/n,120";
    private const string HangupEverything = "channel request hangup all";

    [Fact]
    public async Task Status_ShouldCarryOneNonEmptyLinkedIdForEveryLeg_WhenOneBridgedCallIsUp()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        try
        {
            await OriginateBridgedCallAsync(connection);
            var legs = await WaitForStatusLegsAsync(connection, atLeast: 2);

            legs.Should().HaveCountGreaterThanOrEqualTo(2,
                "the originate builds a call with at least two legs");

            legs.Should().OnlyContain(leg => !string.IsNullOrEmpty(leg.LinkedId),
                "design D4 passes StatusEvent.LinkedId through the reload, which is a correlation "
                + "identifier only if Asterisk populates it on every leg");

            legs.Select(leg => leg.LinkedId ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Should().HaveCount(1,
                    "every leg of one call must report the same Linkedid, or the reload splits one "
                    + "call into several sessions");

            // The wire header is "Linkedid"; StatusEvent exposes it as LinkedId. Asserting the raw
            // field too keeps a mapping regression distinguishable from Asterisk dropping the header.
            legs.Should().OnlyContain(
                leg => leg.RawFields != null && leg.RawFields.ContainsKey("Linkedid"),
                "the Status frame itself must carry the Linkedid header");
        }
        finally
        {
            await BestEffort.SendAsync(connection, new CommandAction { Command = HangupEverything });
        }
    }

    [Fact]
    public async Task Reconnect_ShouldNotReplayTheMissedHangups_WhenTheCallEndedDuringTheOutage()
    {
        // The control connection never goes through the proxy, so it can hang the call up while the
        // connection under test is down — the same shape as a call ending on its own mid-outage.
        await using var control = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await control.ConnectAsync();

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });
        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var afterReconnect = new HangupCollector();
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? resubscription = null;

        // Subscribing inside the handler keeps the gap between "the socket is back" and "we are
        // listening again" as small as the SDK allows: a reconnect hands out a new subscription,
        // which is why VerbaraServer.OnReconnected re-subscribes as well.
        connection.Reconnected += () =>
        {
            resubscription = connection.Subscribe(afterReconnect);
            reconnected.TrySetResult();
        };

        try
        {
            await OriginateBridgedCallAsync(control);
            var legs = await WaitForStatusLegsAsync(connection, atLeast: 2);
            var doomed = legs
                .Select(leg => leg.UniqueId ?? string.Empty)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            doomed.Should().NotBeEmpty(
                "the connection under test must be able to see the call before the cut");

            // reset_peer destroys the established connection and resets every attempt to
            // re-establish it, so the outage lasts until the toxic is removed.
            await ToxiproxyControl.AddToxicAsync(ProxyName, "reload-premise-cut", "reset_peer", "downstream",
                new Dictionary<string, object> { ["timeout"] = 0 });

            using var downCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (connection.State == AmiConnectionState.Connected && !downCts.IsCancellationRequested)
            {
                await BestEffort.SendAsync(connection, new PingAction(), downCts.Token);
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), downCts.Token); }
                catch (OperationCanceledException) { break; }
            }

            connection.State.Should().NotBe(AmiConnectionState.Connected,
                "the call must end while the connection under test is genuinely down");

            await control.SendActionAsync(new CommandAction { Command = HangupEverything });
            await Task.Delay(TimeSpan.FromSeconds(3));

            await ToxiproxyControl.RemoveToxicAsync(ProxyName, "reload-premise-cut");

            using var upCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var upRegistration = upCts.Token.Register(() => reconnected.TrySetCanceled());
            await reconnected.Task;
            connection.State.Should().Be(AmiConnectionState.Connected);

            // Everything Asterisk has to say after the reconnect gets this long to arrive.
            await Task.Delay(TimeSpan.FromSeconds(10));

            afterReconnect.Hangups.Should().NotIntersectWith(doomed,
                "requirement 2 assumes the reload is the last notification such a call produces; a "
                + "replayed Hangup for a channel that died during the outage would narrow that "
                + "premise and make the reload no longer the only ending a consumer sees");

            // And the reload itself must report the call as gone, which is what lets the diff end it.
            var afterLegs = await ReadStatusLegsAsync(connection);
            afterLegs.Select(leg => leg.UniqueId ?? string.Empty).Should().NotIntersectWith(doomed,
                "a Status reload after the reconnect must not report a channel Asterisk no longer has");

            // Positive control for the negative assertion above: the post-reconnect subscription
            // must be able to deliver a Hangup at all. Without this, a broken observer would make
            // "Asterisk replayed nothing" pass for the wrong reason.
            await OriginateBridgedCallAsync(connection);
            var liveLegs = await WaitForStatusLegsAsync(connection, atLeast: 2);
            var live = liveLegs
                .Select(leg => leg.UniqueId ?? string.Empty)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            live.Should().NotBeEmpty("the control call must come up on the reconnected connection");

            await connection.SendActionAsync(new CommandAction { Command = HangupEverything });

            using var controlCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!afterReconnect.Hangups.Intersect(live, StringComparer.Ordinal).Any()
                   && !controlCts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), controlCts.Token); }
                catch (OperationCanceledException) { break; }
            }

            afterReconnect.Hangups.Should().IntersectWith(live,
                "the subscription taken after the reconnect must deliver a Hangup for a call that "
                + "ends while the connection is up, or the assertion that no missed Hangup was "
                + "replayed would be measuring a deaf observer");
        }
        finally
        {
            resubscription?.Dispose();
            await ToxiproxyControl.TryRemoveToxicAsync(ProxyName, "reload-premise-cut");
            await BestEffort.SendAsync(control, new CommandAction { Command = HangupEverything });
        }
    }

    private static async Task OriginateBridgedCallAsync(AmiConnection connection) =>
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = BridgedChannel,
            Application = "Dial",
            Data = BridgedDialData,
            CallerId = "Reload Premise <5557777>",
            IsAsync = true,
            Timeout = 20000
        });

    private static async Task<List<StatusEvent>> ReadStatusLegsAsync(AmiConnection connection)
    {
        var legs = new List<StatusEvent>();
        await foreach (var evt in connection.SendEventGeneratingActionAsync(new StatusAction()))
        {
            if (evt is StatusEvent status)
                legs.Add(status);
        }

        return legs;
    }

    private static async Task<List<StatusEvent>> WaitForStatusLegsAsync(AmiConnection connection, int atLeast)
    {
        var legs = new List<StatusEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            legs = await ReadStatusLegsAsync(connection);
            if (legs.Count >= atLeast)
                return legs;

            try { await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        return legs;
    }

    private sealed class HangupCollector : IObserver<ManagerEvent>
    {
        private readonly ConcurrentDictionary<string, byte> _hangups = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Hangups => _hangups.Keys.ToList();

        public void OnNext(ManagerEvent value)
        {
            if (value is HangupEvent hangup && !string.IsNullOrEmpty(hangup.UniqueId))
                _hangups.TryAdd(hangup.UniqueId, 0);
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}

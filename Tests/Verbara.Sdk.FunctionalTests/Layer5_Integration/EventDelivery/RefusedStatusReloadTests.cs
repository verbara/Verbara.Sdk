namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.EventDelivery;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using AmiConnection = Verbara.Sdk.Ami.Connection.AmiConnection;

/// <summary>
/// A Live server whose AMI user may not run <c>Status</c>, against a real Asterisk. The user is <c>nostatus</c> in
/// <c>docker/functional/asterisk-config/manager.conf</c>: read <c>system,call,agent,user,config,originate,reporting,
/// command,dtmf,cdr</c>, as <c>testadmin</c>, so it receives every call event; write <c>agent,originate,user</c>, so
/// Asterisk refuses its <c>Status</c> with <c>Permission denied</c> (Status requires <c>system</c>, <c>call</c> or
/// <c>reporting</c>) while it allows <c>QueueStatus</c> (registered with no class), <c>Agents</c> (<c>agent</c>) and
/// <c>Originate</c> (<c>originate</c>) — measured on Asterisk 20.20.1, 22.9.0 and 23.4.1. The load read the refused
/// <c>Status</c> as a snapshot with no channels and removed every held channel: a live call went 2 → 0.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class RefusedStatusReloadTests : FunctionalTestBase
{
    private const string RestrictedUser = "nostatus";
    private const string RestrictedSecret = "nostatuspass";

    // A two-leg call whose legs land in a real bridge, as ReloadPremiseTests builds it: the /n suffix keeps Asterisk
    // from optimising the local pair away.
    private const string BridgedChannel = "Local/100@test-functional/n";
    private const string BridgedDialData = "Local/100@test-functional/n,120";
    private const string HangupEverything = "channel request hangup all";

    /// <summary>How long the test waits for the call's channels to reach the server through events. A hang bound.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    public RefusedStatusReloadTests() : base("Verbara.Sdk.Live")
    {
    }

    /// <summary>The control: a start whose Status is refused completes, and still loads the queues and the agents.</summary>
    [Fact]
    public async Task StartAsync_ShouldLoadTheQueues_WhenTheUserMayNotRunStatus()
    {
        await using var connection = CreateRestricted();
        await connection.ConnectAsync();
        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());

        var error = await Record.ExceptionAsync(() => server.StartAsync().WaitAsync(Bound));

        using (new AssertionScope())
        {
            error.Should().BeNull("a refused Status does not fail the start");
            server.Queues.QueueCount.Should().BeGreaterThan(0,
                "QueueStatus needs no write class, so the restricted user loads the queues queues.conf defines");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepTheLiveCall_WhenTheUserMayNotRunStatus()
    {
        await using var control = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await control.ConnectAsync();
        await using var connection = CreateRestricted();
        await connection.ConnectAsync();
        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync().WaitAsync(Bound);
        var removed = new ConcurrentQueue<string>();
        server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        try
        {
            await control.SendActionAsync(new OriginateAction
            {
                Channel = BridgedChannel,
                Application = "Dial",
                Data = BridgedDialData,
                CallerId = "Refused Status <5557778>",
                IsAsync = true,
                Timeout = 20000,
            });
            var heldBefore = await WaitForChannelsAsync(server, atLeast: 2);
            removed.Clear();

            await server.RequestInitialStateAsync().AsTask().WaitAsync(Bound);

            using (new AssertionScope())
            {
                heldBefore.Should().BeGreaterThanOrEqualTo(2, "the call's legs reached the server through their events");
                removed.Should().BeEmpty("Asterisk refused the reload's Status, which is no evidence that a leg is gone");
                server.Channels.ChannelCount.Should().Be(heldBefore, "every leg Asterisk still holds is still held");
            }
        }
        finally
        {
            await BestEffort.SendAsync(control, new CommandAction { Command = HangupEverything });
        }
    }

    private AmiConnection CreateRestricted() =>
        AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Username = RestrictedUser;
            opts.Password = RestrictedSecret;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });

    /// <summary>The server's channel count once it holds at least <paramref name="atLeast"/>, or at <see cref="Bound"/>.</summary>
    private static async Task<int> WaitForChannelsAsync(VerbaraServer server, int atLeast)
    {
        using var cts = new CancellationTokenSource(Bound);
        while (server.Channels.ChannelCount < atLeast && !cts.IsCancellationRequested)
        {
            // fence-allow: LOOP-DRIVER — paces the wait for the call's Newchannel events; cts bounds it
            try { await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        // Settle: both legs of each local pair arrive together, so a count read mid-burst could miss one.
        // fence-allow: SETTLE — lets the burst of Newchannel events of one originate finish before the count is read
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        return server.Channels.ChannelCount;
    }
}

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

/// <summary>
/// What <see cref="IAmiConnection.StateChanged"/> announces against a real Asterisk: the chain of an outage the
/// connection recovers from, and the caller's own ending.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AmiStateChangedTests : FunctionalTestBase
{
    /// <summary>A hang bound for a stop, a start and the reconnect that follows them.</summary>
    private static readonly TimeSpan OutageBound = TimeSpan.FromSeconds(120);

    private readonly FunctionalTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AmiStateChangedTests(FunctionalTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceTheOutageAsOneChainEndingInConnectedBeforeReconnected_WhenAsteriskIsStoppedAndStartedAgain()
    {
        await WriteVersionAsync();

        // Through Toxiproxy: its port is stable across the container's stop and start, and its upstream
        // (asterisk:5038 on the Docker network) resolves to the container whatever address it gets.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.Hostname = ToxiproxyControl.ProxyListenHost;
            opts.Port = ToxiproxyControl.ProxyAmiPort;
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
            opts.ReconnectMultiplier = 1.0;
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var log = new NotificationLog();
        connection.StateChanged += log.Add;
        connection.Reconnected += log.AddReconnected;

        try
        {
            await DockerControl.KillContainerAsync();
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }

        await log.Reconnected.WaitAsync(OutageBound);

        var entries = log.Snapshot();
        foreach (var entry in entries)
            _output.WriteLine(entry.ToString());

        var changes = entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();
        using (new AssertionScope())
        {
            changes.Should().NotBeEmpty();
            changes[0].Previous.Should().Be(AmiConnectionState.Connected, "the first change announced is the loss");
            changes[0].Current.Should().Be(AmiConnectionState.Reconnecting);
            changes[0].IsLoss.Should().BeTrue("the connection left Connected without the caller asking");
            changes.Count(c => c.IsLoss).Should().Be(1, "a loss is announced once per outage");
            for (var i = 1; i < changes.Count; i++)
                changes[i].Previous.Should().Be(changes[i - 1].Current, "each change starts where the one before it ended (change {0})", i);

            changes.Should().OnlyContain(c => !c.ByCaller, "the loss and the reconnect loop made every change after the connect");
            changes.Should().NotContain(c => c.IsFinal, "the loop reconnected, so it never gave up");
            changes[^1].Current.Should().Be(AmiConnectionState.Connected, "the outage ends when a reconnect attempt logs in");
            changes[^1].Previous.Should().Be(AmiConnectionState.Connecting);

            entries.Count(e => e.Change is null).Should().Be(1, "one outage produces one Reconnected");
            entries[^1].Change.Should().BeNull("Reconnected is delivered after the reconnect's change to Connected");
            entries[^2].Change!.Current.Should().Be(AmiConnectionState.Connected);
        }

        connection.State.Should().Be(AmiConnectionState.Connected);
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceOnlyChangesMadeByTheCaller_WhenTheConsumerConnectsAndDisposes()
    {
        await WriteVersionAsync();

        var log = new NotificationLog();
        var connection = AmiConnectionFactory.Create(LoggerFactory, opts => opts.AutoReconnect = true);
        try
        {
            connection.StateChanged += log.Add;
            connection.Reconnected += log.AddReconnected;
            await connection.ConnectAsync();
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        // The ending's changes can be delivered after DisposeAsync has returned.
        await log.Disconnected.WaitAsync(TimeSpan.FromSeconds(30));

        var entries = log.Snapshot();
        foreach (var entry in entries)
            _output.WriteLine(entry.ToString());

        using (new AssertionScope())
        {
            entries.Should().OnlyContain(e => e.Change != null, "nothing was lost, so nothing reconnected");
            entries.Select(e => (e.Change!.Previous, e.Change.Current)).Should().Equal(
                [
                    (AmiConnectionState.Initial, AmiConnectionState.Connecting),
                    (AmiConnectionState.Connecting, AmiConnectionState.Connected),
                    (AmiConnectionState.Connected, AmiConnectionState.Disconnecting),
                    (AmiConnectionState.Disconnecting, AmiConnectionState.Disconnected),
                ],
                "the caller's connect and its dispose are the only things that changed the state");
            entries.Should().OnlyContain(e => e.Change!.ByCaller, "the caller's connect and dispose made every change");
            entries.Should().OnlyContain(e => e.Change!.Cause == null, "nothing failed");
            entries.Should().NotContain(e => e.Change!.IsLoss || e.Change.IsFinal);
        }
    }

    private async Task WriteVersionAsync()
    {
        var version = await _fixture.Asterisk.ExecAsync(["asterisk", "-rx", "core show version"]);
        _output.WriteLine(version.Stdout.Trim());
    }
}

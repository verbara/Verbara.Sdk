using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.IntegrationTests.Ami;

[Collection("Integration")]
[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public class AmiHeartbeatIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private AmiConnection? _connection;

    public AmiHeartbeatIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = _fixture.Asterisk.Host,
            Port = _fixture.Asterisk.AmiPort,
            Username = AsteriskFixture.AmiUsername,
            Password = AsteriskFixture.AmiPassword,
            EnableHeartbeat = true,
            HeartbeatInterval = TimeSpan.FromSeconds(3),
            HeartbeatTimeout = TimeSpan.FromSeconds(5)
        });

        _connection = new AmiConnection(
            options,
            new PipelineSocketConnectionFactory(),
            NullLogger<AmiConnection>.Instance);
        await _connection.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public async Task Heartbeat_ShouldKeepConnectionAlive()
    {
        // Wait longer than 2x heartbeat interval to ensure at least one heartbeat fires
        await Task.Delay(TimeSpan.FromSeconds(8));

        _connection!.State.Should().Be(AmiConnectionState.Connected,
            "connection should remain alive after heartbeat interval");

        // Verify connection is functional by sending a Ping
        var response = await _connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [AsteriskAvailableFact]
    public async Task SendEventGeneratingAction_ShouldRespectTimeout()
    {
        // QueueStatusAction should complete quickly (even with no queues configured)
        // This validates DefaultEventTimeout doesn't hang indefinitely
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<ManagerEvent>();

        await foreach (var evt in _connection!.SendEventGeneratingActionAsync(
            new QueueStatusAction(), cts.Token))
        {
            events.Add(evt);
        }

        // Should complete without timeout — the action finishes within DefaultEventTimeout
        events.Should().NotBeNull();
    }
}

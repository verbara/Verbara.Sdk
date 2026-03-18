using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Diagnostics;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.IntegrationTests.Ami;

[Trait("Category", "Integration")]
public class AmiHealthCheckIntegrationTests : IClassFixture<AsteriskFixture>, IAsyncLifetime
{
    private readonly AsteriskFixture _fixture;
    private Asterisk.Sdk.Ami.Connection.AmiConnection? _connection;

    public AmiHealthCheckIntegrationTests(AsteriskFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connection = _fixture.CreateAmiConnection();
        await _connection.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public async Task AmiHealthCheck_ShouldReturnHealthy_WhenConnected()
    {
        var healthCheck = new AmiHealthCheck(_connection!);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [AsteriskAvailableFact]
    public async Task AmiHealthCheck_ShouldReturnUnhealthy_WhenDisconnected()
    {
        await _connection!.DisconnectAsync();

        var healthCheck = new AmiHealthCheck(_connection);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        _connection = null; // Prevent DisposeAsync from double-disconnecting
    }
}

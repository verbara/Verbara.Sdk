using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Diagnostics;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Sdk.IntegrationTests.Ami;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class AmiHealthCheckIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private AmiConnection? _connection;

    public AmiHealthCheckIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connection = AsteriskFixture.CreateAmiConnection(_fixture);
        await _connection.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AmiHealthCheck_ShouldReturnHealthy_WhenConnected()
    {
        var healthCheck = new AmiHealthCheck(_connection!);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AmiHealthCheck_ShouldReturnUnhealthy_WhenDisconnected()
    {
        await _connection!.DisconnectAsync();

        var healthCheck = new AmiHealthCheck(_connection);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        _connection = null; // Prevent DisposeAsync from double-disconnecting
    }
}

using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Agi.Diagnostics;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.IntegrationTests.Agi;

[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal is handled via IAsyncLifetime.DisposeAsync")]
public class AgiHealthCheckIntegrationTests : IAsyncLifetime
{
    private FastAgiServer? _agiServer;

    public async Task InitializeAsync()
    {
        // Use a random high port to avoid conflicts with other tests
        _agiServer = new FastAgiServer(0, new SimpleMappingStrategy(), NullLogger<FastAgiServer>.Instance);
        await _agiServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_agiServer is not null) await _agiServer.DisposeAsync();
    }

    [Fact]
    public async Task AgiHealthCheck_ShouldReturnHealthy_WhenServerRunning()
    {
        var healthCheck = new AgiHealthCheck(_agiServer!);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AgiHealthCheck_ShouldReturnUnhealthy_WhenServerStopped()
    {
        await _agiServer!.StopAsync();

        var healthCheck = new AgiHealthCheck(_agiServer);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        _agiServer = null; // Prevent DisposeAsync from double-stopping
    }
}

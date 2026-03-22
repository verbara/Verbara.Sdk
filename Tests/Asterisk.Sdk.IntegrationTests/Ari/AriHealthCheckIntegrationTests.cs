using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Diagnostics;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.IntegrationTests.Ari;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class AriHealthCheckIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private AriClient? _client;

    public AriHealthCheckIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _client = AsteriskFixture.CreateAriClient(_fixture);
        await _client.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public async Task AriHealthCheck_ShouldReturnHealthy_WhenConnected()
    {
        var healthCheck = new AriHealthCheck(_client!);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AriHealthCheck_ShouldReturnUnhealthy_WhenUnreachable()
    {
        // Create a client with an invalid URL — no real Asterisk needed
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://localhost:1",
            Username = "invalid",
            Password = "invalid",
            Application = "test"
        });
        await using var badClient = new AriClient(options, NullLogger<AriClient>.Instance);

        var healthCheck = new AriHealthCheck(badClient);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}

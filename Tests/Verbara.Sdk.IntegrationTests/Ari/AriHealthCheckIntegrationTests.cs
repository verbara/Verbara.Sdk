using System.Net.WebSockets;
using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.IntegrationTests.Ari;

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

    [Fact]
    public async Task AriHealthCheck_ShouldReturnHealthy_WhenConnected()
    {
        var healthCheck = new AriHealthCheck(_client!);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AriHealthCheck_ShouldReturnDegraded_WhenTheClientWasNeverConnected()
    {
        // A client nobody has connected yet is not a failure: Initial reads Degraded, and names itself.
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            Username = "invalid",
            Password = "invalid",
            Application = "test"
        });
        await using var idle = new AriClient(options, NullLogger<AriClient>.Instance);

        var result = await new AriHealthCheck(idle).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Degraded);
            result.Data.Should().ContainKey("ariState").WhoseValue.Should().Be(nameof(AriConnectionState.Initial));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AriHealthCheck_ShouldReturnUnhealthy_WhenTheConnectToAnUnreachableAsteriskFailed()
    {
        // Port 1 on loopback has no listener: the dial is refused, the attempt ends Faulted, and nothing
        // reconnects the client until a new connect.
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            Username = "invalid",
            Password = "invalid",
            Application = "test"
        });
        await using var badClient = new AriClient(options, NullLogger<AriClient>.Instance);
        var connect = async () => await badClient.ConnectAsync();
        await connect.Should().ThrowAsync<WebSocketException>("nothing listens on the port");

        var result = await new AriHealthCheck(badClient).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Data.Should().ContainKey("ariState").WhoseValue.Should().Be(nameof(AriConnectionState.Faulted));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConnectAsync_ShouldLeaveStateFaulted_WhenAsteriskRefusesTheCredentials()
    {
        // The fixture's own Asterisk, on its own ARI port, with one variable changed: the password.
        // The sibling test above proves the unmodified pair connects, so a refusal here is the real
        // res_ari answering real credentials it does not accept — not a port that is not listening.
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://{_fixture.Asterisk.Host}:{_fixture.Asterisk.AriPort}",
            Username = AsteriskFixture.AriUsername,
            Password = AsteriskFixture.AriPassword + "-refused",
            Application = AsteriskFixture.AriApp
        });
        await using var refused = new AriClient(options, NullLogger<AriClient>.Instance);

        var connect = async () => await refused.ConnectAsync();
        await connect.Should().ThrowAsync<WebSocketException>("a refused upgrade never becomes a connection");

        var health = await new AriHealthCheck(refused).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            refused.State.Should().Be(
                AriConnectionState.Faulted, "an attempt that ended without a connection is over");
            refused.IsConnected.Should().BeFalse();
            health.Status.Should().Be(HealthStatus.Unhealthy);
            health.Description.Should().Contain("Faulted", "the health message names the terminal state");
        }
    }
}

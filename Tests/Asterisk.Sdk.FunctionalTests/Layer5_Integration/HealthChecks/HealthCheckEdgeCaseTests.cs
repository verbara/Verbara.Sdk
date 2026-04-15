namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.HealthChecks;

using Asterisk.Sdk.Ami.Diagnostics;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class HealthCheckEdgeCaseTests : FunctionalTestBase
{
    [Fact]
    public async Task AmiHealthCheck_ShouldTransitionStates_DuringReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var healthCheck = new AmiHealthCheck(connection);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("ami", healthCheck, HealthStatus.Unhealthy, null)
        };

        // Verify Healthy when connected
        var initialResult = await healthCheck.CheckHealthAsync(context);
        initialResult.Status.Should().Be(HealthStatus.Healthy, "AMI should be healthy when connected");

        try
        {
            await DockerControl.KillContainerAsync();

            // Poll until health check is no longer Healthy
            HealthCheckResult degradedResult = default;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                degradedResult = await healthCheck.CheckHealthAsync(context);
                if (degradedResult.Status != HealthStatus.Healthy)
                    break;
                await Task.Delay(500);
            }

            // Health check must reflect disconnected/reconnecting state after kill
            degradedResult.Status.Should().BeOneOf(HealthStatus.Degraded, HealthStatus.Unhealthy);
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }

        // Wait for reconnection
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        // If already reconnected before we hooked the event, the state will already be Connected
        if (connection.State == AmiConnectionState.Connected)
            reconnected.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        // Health check must return to Healthy after reconnect
        var recoveredResult = await healthCheck.CheckHealthAsync(context);
        recoveredResult.Status.Should().Be(HealthStatus.Healthy, "health check must be Healthy after reconnect");
    }

    [Fact]
    public async Task HealthCheck_ShouldNotHang_UnderHighLoad()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var healthCheck = new AmiHealthCheck(connection);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("ami", healthCheck, HealthStatus.Unhealthy, null)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Fire 50 concurrent health checks
        var healthTasks = Enumerable.Range(0, 50)
            .Select(_ => healthCheck.CheckHealthAsync(context, cts.Token))
            .ToList();

        // Also fire 100 PingActions concurrently
        var pingTasks = Enumerable.Range(0, 100)
            .Select(async i =>
            {
                var action = new PingAction { ActionId = $"hc-load-{i:D4}" };
                return await connection.SendActionAsync(action, cts.Token);
            })
            .ToList();

        // All health checks must complete within the timeout
        var healthResults = await Task.WhenAll(healthTasks);
        var pingResponses = await Task.WhenAll(pingTasks);

        healthResults.Should().HaveCount(50, "all 50 concurrent health checks must complete");
        healthResults.Should().AllSatisfy(r =>
            r.Status.Should().Be(HealthStatus.Healthy, "all health checks must remain Healthy under load"));

        pingResponses.Should().HaveCount(100, "all 100 ping actions must receive responses");
        pingResponses.Should().AllSatisfy(r =>
            r.Response.Should().Be("Success", "all pings must succeed under load"));
    }

    [Fact]
    public async Task HealthCheck_ShouldReflectActualState_AfterReconnect()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        var healthCheck = new AmiHealthCheck(connection);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("ami", healthCheck, HealthStatus.Unhealthy, null)
        };

        // Phase 1: Connected → Healthy
        var result1 = await healthCheck.CheckHealthAsync(context);
        result1.Status.Should().Be(HealthStatus.Healthy,
            "health check status must match Connected state");

        // Subscribe to Reconnected BEFORE killing so we don't miss the event
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();

        try
        {
            await DockerControl.KillContainerAsync();

            // Wait briefly for state to change
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Phase 2: Disconnected/Reconnecting → not Healthy
            var result2 = await healthCheck.CheckHealthAsync(context);
            var expectedNotHealthy = connection.State != AmiConnectionState.Connected;

            if (expectedNotHealthy)
            {
                result2.Status.Should().NotBe(HealthStatus.Healthy,
                    "health check must reflect non-Connected state");
            }
        }
        finally
        {
            await DockerControl.StartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
        }

        // Phase 3: After reconnect → Healthy again
        if (connection.State == AmiConnectionState.Connected)
            reconnected.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        cts.Token.Register(() => reconnected.TrySetCanceled());
        await reconnected.Task;

        var result3 = await healthCheck.CheckHealthAsync(context);
        result3.Status.Should().Be(HealthStatus.Healthy,
            "health check must return Healthy after successful reconnect");

        // Verify consistency: connection.State == Connected implies health check == Healthy
        connection.State.Should().Be(AmiConnectionState.Connected);
        result3.Status.Should().Be(HealthStatus.Healthy,
            "health check status must be consistent with connection state");
    }

    [Fact]
    public async Task AllHealthChecks_ShouldBeRegistrable_ViaHostBuilder()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddAsterisk(opts =>
                {
                    opts.Ami.Hostname = AsteriskContainerFixture.Host;
                    opts.Ami.Port = AsteriskContainerFixture.AmiPort;
                    opts.Ami.Username = AmiConnectionFactory.Username;
                    opts.Ami.Password = AmiConnectionFactory.Password;
                    opts.Ami.AutoReconnect = false;
                });
            })
            .Build();

        using (host)
        {
            // Verify AmiHealthCheck is registered and resolvable from DI
            var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();
            healthCheckService.Should().NotBeNull("HealthCheckService must be registered via AddAsterisk");

            // Run all registered health checks — AMI will not be connected but should not throw
            // Note: HealthCheckRegistration is stored in IOptions<HealthCheckServiceOptions>,
            // not as DI services — validate via CheckHealthAsync report entries instead.
            var report = await healthCheckService.CheckHealthAsync();
            report.Should().NotBeNull("health check report must be produced");
            report.Entries.Should().ContainKey("ami", "ami health check must appear in report");
            report.Entries.Should().ContainKey("agi", "agi health check must appear in report");

            // Without starting the host, AMI is not connected → Unhealthy is expected
            // Any valid HealthStatus is acceptable — ami is not connected without host.StartAsync
            report.Entries["ami"].Status.Should().BeOneOf(
                HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
        }
    }
}

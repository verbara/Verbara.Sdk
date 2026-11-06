using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host whose only Asterisk registration is <c>AddVerbaraAri</c>, with events off, starts and stops with no Asterisk
/// reachable and no service replaced, and keeps its <c>ari</c> entry, which reads <c>Unhealthy</c> because it asks.
/// <see cref="RestOnlyAriHostStartTests"/> proves the ARI part of an <c>AddVerbara</c> host, whose AMI prerequisite is
/// met there with a substitute; here there is no AMI to meet.
/// </summary>
public sealed class AriOnlyHostStartTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HostStartAsync_ShouldStartAndStopWithNoAsterisk_WhenOnlyAriIsRegisteredWithEventsOff()
    {
        using var host = Build(eventsEnabled: false);

        var start = () => host.StartAsync().WaitAsync(Bound);
        await start.Should().NotThrowAsync("an ARI-only client with events off needs no reachable Asterisk to start");
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync().WaitAsync(Bound);
        var client = host.Services.GetRequiredService<IAriClient>();
        var stateWhileUp = client.State;
        var stop = () => host.StopAsync().WaitAsync(Bound);
        await stop.Should().NotThrowAsync();

        using (new AssertionScope())
        {
            host.Services.GetService<IAmiConnection>().Should().BeNull("no AMI connection is registered, nor replaced");
            stateWhileUp.Should().Be(AriConnectionState.RestOnly);
            report.Entries.Keys.Should().Equal(["ari"], "the ari check is the host's only SDK check");
            report.Entries["ari"].Status.Should().Be(HealthStatus.Unhealthy, "it asked, and nothing answered");
            report.Entries["ari"].Data["ariState"].Should().Be("RestOnly");
        }
    }

    [Fact]
    public async Task HostStartAsync_ShouldFail_WhenEventsAreOn()
    {
        // Control: the same host with events on takes the start down, which shows the scenario above could fail.
        using var host = Build(eventsEnabled: true);

        var start = () => host.StartAsync().WaitAsync(Bound);

        await start.Should().ThrowAsync<Exception>("a client with events dials at start");
    }

    private static IHost Build(bool eventsEnabled)
    {
        var baseUrl = UnreachableBaseUrl();
        return new HostBuilder()
            .ConfigureServices(services => services.AddVerbaraAri(o =>
            {
                o.BaseUrl = baseUrl;
                o.Username = "asterisk";
                o.Password = "secret";
                o.Application = "ari-only";
                o.EventsEnabled = eventsEnabled;
            }))
            .Build();
    }

    /// <summary>A port nothing listens on: bound, read and released here.</summary>
    private static string UnreachableBaseUrl()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
    }
}

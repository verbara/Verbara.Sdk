using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host built by <c>AddVerbara</c> whose ARI client has its events off starts and stops with no ARI listener, and
/// keeps its <c>ari</c> health entry, which then reads <c>Unhealthy</c> because it asks. What this bounds is ARI's own
/// part: the AMI connection still dials at start, so that prerequisite is met here with a substitute.
/// </summary>
public sealed class RestOnlyAriHostStartTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HostStartAsync_ShouldStartAndStopAndReportAriUnhealthy_WhenEventsAreOffAndNoListenerIsReachable()
    {
        using var host = Build(eventsEnabled: false);

        var start = () => host.StartAsync().WaitAsync(Bound);
        await start.Should().NotThrowAsync("the ARI client, its hosted service and its check need no reachable Asterisk");
        var report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(entry => entry.Name == "ari");
        var client = host.Services.GetRequiredService<IAriClient>();
        var stateWhileUp = client.State;
        var stop = () => host.StopAsync().WaitAsync(Bound);
        await stop.Should().NotThrowAsync();

        using (new AssertionScope())
        {
            stateWhileUp.Should().Be(AriConnectionState.RestOnly);
            report.Entries.Should().ContainKey("ari", "the check stays registered");
            report.Entries["ari"].Status.Should().Be(HealthStatus.Unhealthy, "it asked, and nothing answered");
            report.Entries["ari"].Data["ariState"].Should().Be("RestOnly");
            client.State.Should().Be(AriConnectionState.RestOnly, "stopping the host disconnects nothing on this client");
        }
    }

    [Fact]
    public async Task HostStartAsync_ShouldFail_WhenEventsAreOnAndNoListenerIsReachable()
    {
        // Control: the same host with events on takes the start down, which shows the scenario above could fail.
        using var host = Build(eventsEnabled: true);

        var start = () => host.StartAsync().WaitAsync(Bound);

        await start.Should().ThrowAsync<Exception>("a client with events dials at start");
    }

    [Fact]
    public async Task AddVerbara_ShouldHandOutARestOnlyClient_WhenTheOptionIsSetInline()
    {
        using var host = Build(eventsEnabled: false);

        await using var client = host.Services.GetRequiredService<IAriClient>();

        client.State.Should().Be(AriConnectionState.RestOnly);
    }

    [Fact]
    public async Task AddVerbara_ShouldHandOutARestOnlyClient_WhenTheOptionComesFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Asterisk:Ami:Hostname"] = "localhost",
                ["Asterisk:Ami:Username"] = "admin",
                ["Asterisk:Ami:Password"] = "secret",
                ["Asterisk:AgiPort"] = "0",
                ["Asterisk:Ari:BaseUrl"] = "http://127.0.0.1:8088",
                ["Asterisk:Ari:Username"] = "asterisk",
                ["Asterisk:Ari:Password"] = "secret",
                ["Asterisk:Ari:Application"] = "rest-only",
                ["Asterisk:Ari:EventsEnabled"] = "false",
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Connection());
                services.AddVerbara(configuration);
            })
            .Build();

        await using var client = host.Services.GetRequiredService<IAriClient>();

        client.State.Should().Be(AriConnectionState.RestOnly);
    }

    private static IHost Build(bool eventsEnabled)
    {
        var baseUrl = UnreachableBaseUrl();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Connection());
                services.AddVerbara(o =>
                {
                    o.Ami.Hostname = "localhost";
                    o.Ami.Username = "admin";
                    o.Ami.Password = "secret";
                    o.AgiPort = 0;
                    o.Ari = new AriClientOptions
                    {
                        BaseUrl = baseUrl,
                        Username = "asterisk",
                        Password = "secret",
                        Application = "rest-only",
                        EventsEnabled = eventsEnabled,
                    };
                });
            })
            .Build();
    }

    private static IAmiConnection Connection()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.9.0");
        connection.State.Returns(AmiConnectionState.Connected);
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(_ => Substitute.For<IDisposable>());
        connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => Nothing());
        return connection;
    }

    private static async IAsyncEnumerable<ManagerEvent> Nothing(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    /// <summary>A port nothing listens on: bound, read and released here.</summary>
    private static string UnreachableBaseUrl()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
    }
}

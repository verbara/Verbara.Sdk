using System.Text.Json;

using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Nats;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace Verbara.Sdk.Push.Nats.IntegrationTests;

/// <summary>
/// Counts the NATS client connections a bidirectional <see cref="NatsBridge"/> holds, as reported by
/// the server's <c>/connz</c> monitoring endpoint. The class gets its own NATS server (a class
/// fixture, not the shared <c>Nats</c> collection), so connections that other tests leave open
/// cannot enter the count.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NatsBridgeConnectionTests(NatsContainerFixture fixture) : IClassFixture<NatsContainerFixture>
{
    private const string Prefix = "asterisk.sdk.connections";
    private const string Filter = Prefix + ".>";

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Bridge_ShouldHoldOneConnectionAndCloseIt_WhenSubscribeIsEnabled()
    {
        using var http = new HttpClient();
        ConnzSnapshot running;

        var host = BuildHost();
        try
        {
            await host.StartAsync();

            // The bridge connects its publisher before the subscribe loop subscribes to the filter,
            // so once the server lists that subscription every connection the bridge opens is open.
            running = await WaitForAsync(http, static s => s.Subscriptions.Contains(Filter));

            await host.StopAsync();
        }
        finally
        {
            host.Dispose();
        }

        var afterStopAndDispose = await WaitForAsync(http, static s => s.Connections == 0);

        (running.Connections, running.Subscriptions.Count(s => s == Filter), afterStopAndDispose.Connections)
            .Should().Be(
                (1, 1, 0),
                "the bridge shares one connection and closes it on stop (subscriptions while running: {0})",
                string.Join(", ", running.Subscriptions));
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVerbaraPush();
        builder.Services.AddPushNats(opt =>
        {
            opt.Url = fixture.Url;
            opt.SubjectPrefix = Prefix;
            opt.ConnectTimeoutSeconds = 5;
            opt.NodeId = "nodeA";
            opt.Subscribe = new NatsSubscribeOptions
            {
                SubjectFilters = [Filter],
                SkipSelfOriginated = true,
            };
        });
        return builder.Build();
    }

    /// <summary>
    /// Polls <c>/connz</c> until <paramref name="done"/> holds or <see cref="SettleTimeout"/> passes,
    /// and returns the last snapshot either way, so the assertion reports what the server saw.
    /// </summary>
    private async Task<ConnzSnapshot> WaitForAsync(HttpClient http, Func<ConnzSnapshot, bool> done)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;
        while (true)
        {
            var snapshot = await ReadConnzAsync(http);
            if (done(snapshot) || DateTime.UtcNow >= deadline)
                return snapshot;
            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Reads the open client connections and every subject they subscribe to.</summary>
    private async Task<ConnzSnapshot> ReadConnzAsync(HttpClient http)
    {
        await using var body = await http.GetStreamAsync(new Uri($"{fixture.MonitorUrl}/connz?subs=1"));
        using var doc = await JsonDocument.ParseAsync(body);

        var connections = 0;
        var subscriptions = new List<string>();
        if (doc.RootElement.TryGetProperty("connections", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var connection in list.EnumerateArray())
            {
                connections++;
                if (connection.TryGetProperty("subscriptions_list", out var subjects)
                    && subjects.ValueKind == JsonValueKind.Array)
                {
                    foreach (var subject in subjects.EnumerateArray())
                        subscriptions.Add(subject.GetString() ?? string.Empty);
                }
            }
        }

        return new ConnzSnapshot(connections, subscriptions);
    }

    private sealed record ConnzSnapshot(int Connections, IReadOnlyList<string> Subscriptions);
}

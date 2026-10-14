using System.Globalization;
using System.Net.WebSockets;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Diagnostics;

/// <summary>
/// The <c>openai-realtime</c> health entry reports what the vendor did with the bridge's sessions, not only whether a
/// key is configured: <c>Degraded</c> after three consecutive rejected sessions, <c>Healthy</c> again on the first
/// served one, never <c>Unhealthy</c> for rejections; a session the host cancelled, or one that ended on a transport
/// failure, counts neither way.
/// </summary>
/// <remarks>
/// Each test builds the registration the SDK ships (<c>AddOpenAiRealtimeBridge</c>), runs sessions through the bridge
/// that registration resolves, against the in-tree fake vendor, and reads the entry through
/// <see cref="HealthCheckService"/>, as a host's health endpoint does. No test waits on a clock: each session is ended
/// by a signal the test sends, and awaited to its end before the next one starts. The bridge runs on a manual clock,
/// so the time of a rejection is a value the test knows.
/// </remarks>
public sealed class RealtimeHealthCheckTests : IAsyncLifetime
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset ClockStart = new(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

    private const string EntryName = "openai-realtime";

    private static readonly string AudioDelta =
        $$"""{"type":"response.output_audio.delta","delta":"{{Convert.ToBase64String(new byte[960])}}"}""";

    private ServiceProvider? _provider;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheVendorRejectedTheLastThreeSessions()
    {
        var (bridge, health) = Build();

        for (var i = 0; i < 3; i++)
            await RunRejectedSessionAsync(bridge, closeCode: 3000);

        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            entry.Status.Should().Be(HealthStatus.Degraded, "the vendor rejected the last three sessions");
            entry.Data.Should().Contain("consecutiveRejections", 3);
            entry.Data.Should().Contain("lastRejection", 3000, "the vendor's close code");
            entry.Data.Should().Contain(
                "lastRejectionAt", ClockStart.ToString("O", CultureInfo.InvariantCulture), "when that rejection happened");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenASessionIsServedAfterRejections()
    {
        var (bridge, health) = Build();

        for (var i = 0; i < 3; i++)
            await RunRejectedSessionAsync(bridge, closeCode: 3000);
        var before = (await CheckAsync(health)).Status;

        await RunServedSessionAsync(bridge);
        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            before.Should().Be(HealthStatus.Degraded, "three rejections came first");
            entry.Status.Should().Be(HealthStatus.Healthy, "the vendor served the next session");
            entry.Data.Should().Contain("consecutiveRejections", 0);
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldNotCountTheSession_WhenTheHostCancelsIt()
    {
        var (bridge, health) = Build();

        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunCancelledSessionAsync(bridge);

        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            entry.Status.Should().Be(HealthStatus.Healthy, "two rejections are below the threshold");
            entry.Data.Should().Contain("consecutiveRejections", 2, "a session the host cancelled counts neither way");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldNotCountTheSession_WhenItEndsOnATransportFailure()
    {
        var (bridge, health) = Build();

        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunDroppedSessionAsync(bridge);

        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            entry.Status.Should().Be(HealthStatus.Healthy, "two rejections are below the threshold");
            entry.Data.Should().Contain("consecutiveRejections", 2, "a transport failure counts neither way");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheVendorRefusedTheLastThreeUpgrades()
    {
        var (bridge, health) = Build();

        for (var i = 0; i < 3; i++)
            await RunRefusedUpgradeAsync(bridge);

        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            entry.Status.Should().Be(HealthStatus.Degraded, "the vendor refused the last three upgrades");
            entry.Data.Should().Contain("consecutiveRejections", 3);
            entry.Data.Should().Contain("lastRejection", 401, "the upgrade's HTTP status");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldCountTheSessionServed_WhenTheVendorClosesWithAFailureAfterItsAudioReachedTheCaller()
    {
        // A failure close is a rejection only before any vendor audio reached the caller: after it, the vendor had
        // accepted the session, so the session ends the streak.
        var (bridge, health) = Build();

        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunRejectedSessionAsync(bridge, closeCode: 3000);
        await RunFailedAfterAudioSessionAsync(bridge, closeCode: 4000);

        var entry = await CheckAsync(health);

        using (new AssertionScope())
        {
            entry.Status.Should().Be(HealthStatus.Healthy);
            entry.Data.Should().Contain("consecutiveRejections", 0, "the vendor served that session before it failed");
            entry.Data.Should().Contain("lastRejection", 3000, "the last rejection is still reported");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthyWithoutData_WhenBuiltByHandWithAKey()
    {
        // A guard, green before the tracker existed: built by hand, the check reads the configuration only.
        var sut = new RealtimeHealthCheck(Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key", Model = "gpt-realtime" }));

        var result = await sut.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(EntryName, sut, failureStatus: null, tags: null),
        });

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Healthy, "a key is configured");
            result.Data.Should().BeEmpty("a check built by hand sees no session");
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenNoApiKeyIsConfigured()
    {
        // A guard, green before the tracker existed: the check built by hand behaves as it always did.
        var sut = new RealtimeHealthCheck(Options.Create(new OpenAiRealtimeOptions { ApiKey = "" }));

        var result = await sut.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(EntryName, sut, failureStatus: null, tags: null),
        });

        result.Status.Should().Be(HealthStatus.Unhealthy, "no key is configured");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (OpenAiRealtimeBridge Bridge, HealthCheckService Health) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAudioSocketServer(o => o.Port = 0);
        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey = "test-key";
            o.Model = "gpt-realtime";
            o.InputFormat = Audio.AudioFormat.Slin16Mono8kHz;
        });
        _provider = services.BuildServiceProvider();

        var bridge = _provider.GetRequiredService<OpenAiRealtimeBridge>();
        bridge.TimeProvider = new FakeTimeProvider(ClockStart);
        return (bridge, _provider.GetRequiredService<HealthCheckService>());
    }

    private static async Task<HealthReportEntry> CheckAsync(HealthCheckService health)
    {
        var report = await health.CheckHealthAsync(r => r.Name == EntryName);
        return report.Entries[EntryName];
    }

    /// <summary>The vendor answers the upgrade, then closes with <paramref name="closeCode"/> before any audio.</summary>
    private static async Task RunRejectedSessionAsync(OpenAiRealtimeBridge bridge, int closeCode)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        await RunSessionAsync(bridge, fake.Port, async _ =>
        {
            await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fake.SendCloseAsync((WebSocketCloseStatus)closeCode, "rejected");
        });
    }

    /// <summary>The vendor sends audio, which reaches the caller, and then closes with <c>1000</c>.</summary>
    private static async Task RunServedSessionAsync(OpenAiRealtimeBridge bridge)
    {
        await using var fake = new RealtimeFakeServer();
        fake.EventsToSend.Add(AudioDelta);
        fake.Start();
        await RunSessionAsync(bridge, fake.Port, _ => Task.CompletedTask);
    }

    /// <summary>The vendor sends audio, which reaches the caller, and then closes with <paramref name="closeCode"/>.</summary>
    private static async Task RunFailedAfterAudioSessionAsync(OpenAiRealtimeBridge bridge, int closeCode)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        await RunSessionAsync(bridge, fake.Port, async _ =>
        {
            await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fake.SendEventAsync(AudioDelta);
            await fake.SendCloseAsync((WebSocketCloseStatus)closeCode, "server_error");
        });
    }

    /// <summary>The host cancels the session while the vendor holds it open, having answered nothing.</summary>
    private static async Task RunCancelledSessionAsync(OpenAiRealtimeBridge bridge)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        await RunSessionAsync(bridge, fake.Port, async host =>
        {
            await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await host.CancelAsync();
        });
    }

    /// <summary>The vendor's connection drops mid-session, with no close.</summary>
    private static async Task RunDroppedSessionAsync(OpenAiRealtimeBridge bridge)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        await RunSessionAsync(bridge, fake.Port, async _ =>
        {
            await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);
            fake.Abort();
        });
    }

    /// <summary>The vendor refuses the upgrade with <c>401</c>.</summary>
    private static async Task RunRefusedUpgradeAsync(OpenAiRealtimeBridge bridge)
    {
        await using var listener = new RejectingHandshakeListener();
        listener.Start();
        await RunSessionAsync(bridge, listener.Port, _ => Task.CompletedTask);
    }

    /// <summary>
    /// Runs one session against the vendor on <paramref name="vendorPort"/>, lets <paramref name="drive"/> end it, and
    /// waits for the session to return, whatever it returned with.
    /// </summary>
    private static async Task RunSessionAsync(
        OpenAiRealtimeBridge bridge, int vendorPort, Func<CancellationTokenSource, Task> drive)
    {
        bridge.BaseUri = new Uri($"ws://127.0.0.1:{vendorPort}/");
        var (session, audioServer, client) = await CreateAudioSessionAsync();
        using var host = new CancellationTokenSource();
        try
        {
            var run = bridge.HandleSessionAsync(session, host.Token).AsTask();
            await drive(host);
            _ = await Record.ExceptionAsync(() => run.WaitAsync(SignalTimeout));
            run.IsCompleted.Should().BeTrue("each session ends before the next one starts");
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<(AudioSocketSession Session, AudioSocketServer Server, AudioSocketClient Client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var started = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        audioServer.OnSessionStarted += session =>
        {
            started.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await started.Task.WaitAsync(SignalTimeout);
        return (session, audioServer, client);
    }
}

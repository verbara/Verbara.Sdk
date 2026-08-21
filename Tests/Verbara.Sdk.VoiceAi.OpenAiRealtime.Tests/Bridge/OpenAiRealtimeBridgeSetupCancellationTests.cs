using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// The setup window: everything between entering <c>HandleSessionAsync</c> and the loops starting.
/// A cancel landing there used to escape as a fault and skip the terminal block entirely, because
/// the <c>finally</c> that owns the close, the completion counter, the duration histogram and the
/// end-of-session log was attached to the <em>inner</em> try. See ADR-0053.
/// </summary>
public sealed class OpenAiRealtimeBridgeSetupCancellationTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    [Fact]
    public async Task HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenCancelledDuringConnect()
    {
        // Arrange — the peer accepts the TCP connection and never sends the 101, so the connect can
        // only be ended by the token. Nothing here waits on a clock.
        await using var stalled = new StalledHandshakeListener();
        stalled.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var log = new RecordingLogger<OpenAiRealtimeBridge>();
        await using var bridge = CreateBridge(stalled.Port, log);
        using var metrics = new MeterCapture(MeterName);
        using var cts = new CancellationTokenSource();

        // Act — the cancelled token goes to the subject and nowhere else (ADR-0052 F3)
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await stalled.RequestReceived.WaitAsync(SignalTimeout);   // the connect is demonstrably in flight
        await cts.CancelAsync();

        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert — all of it in one scope, so a pre-fix run reports the whole defect at once
        using (new AssertionScope())
        {
            fault.Should().BeNull("a cancellation the caller asked for is not a fault");
            metrics.Get("openai_realtime.sessions.started").Should().Be(1);
            metrics.Get("openai_realtime.sessions.completed").Should()
                .Be(1, "the terminal block must run wherever the cancel landed");
            metrics.Get("openai_realtime.sessions.failed").Should().Be(0);
            metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(0);
            log.Entries.Should().Contain(e => e.EventId.Name == "SessionEnded");
            log.Entries.Should().NotContain(
                e => e.EventId.Name == "WebSocketConnected",
                "the handshake never completed, so no transport was ever open to close politely");
        }

        await CleanupAsync(client, audioServer);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenCancelledBeforeConnect()
    {
        // Arrange — the entry edge: the token is already cancelled when the bridge is called.
        await using var stalled = new StalledHandshakeListener();
        stalled.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var log = new RecordingLogger<OpenAiRealtimeBridge>();
        await using var bridge = CreateBridge(stalled.Port, log);
        using var metrics = new MeterCapture(MeterName);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var fault = await Record.ExceptionAsync(
            () => bridge.HandleSessionAsync(session, cts.Token).AsTask().WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull("a cancellation the caller asked for is not a fault");
            metrics.Get("openai_realtime.sessions.completed").Should().Be(1);
            metrics.Get("openai_realtime.sessions.failed").Should().Be(0);
            log.Entries.Should().Contain(e => e.EventId.Name == "SessionEnded");
        }

        await CleanupAsync(client, audioServer);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>();
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(SignalTimeout);
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(int port, ILogger<OpenAiRealtimeBridge> logger)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "alloy",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        var bridge = new OpenAiRealtimeBridge(
            options, new RealtimeFunctionRegistry([]), logger)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{port}/"),
        };
        return bridge;
    }

    private static async Task CleanupAsync(AudioSocketClient client, AudioSocketServer audioServer)
    {
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
        await audioServer.DisposeAsync();
    }
}

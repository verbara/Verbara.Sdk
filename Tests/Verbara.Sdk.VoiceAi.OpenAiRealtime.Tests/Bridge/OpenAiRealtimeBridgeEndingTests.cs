using System.Net.WebSockets;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// How a Realtime session ends when one of its two far ends ends it: the caller, through the
/// AudioSocket session, or the vendor, through the WebSocket. Every test here runs on a session token
/// that is never cancelled, so the ending under test is the only one there is.
/// </summary>
/// <remarks>
/// <para>
/// The vendor never closes a healthy session on its own. Measured live on 2026-09-27, the bridge was
/// still waiting on it sixty seconds after the caller hung up, with no completion counted and no
/// duration recorded; when the bridge closed first, the vendor answered with <c>1000</c> about 1.1 s
/// later. The fake's <see cref="RealtimeFakeServer.HoldOpenUntilClientCloses"/> is that vendor, and
/// <see cref="RealtimeFakeServer.HoldOpenUntilDisposed"/> is one that never answers.
/// </para>
/// <para>
/// No test here waits on a clock. Each waits on the signal it asserts, bounded by
/// <see cref="SignalTimeout"/>, whose expiry is a failure.
/// </para>
/// </remarks>
public sealed class OpenAiRealtimeBridgeEndingTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An event the fake delivers so that the bridge publishes something once its output loop runs.
    /// Never asserted on; see <c>OpenAiRealtimeBridgeTests.LoopsRunningMarkerEvent</c>.
    /// </summary>
    private const string LoopsRunningMarkerEvent = """{"type":"input_audio_buffer.speech_started"}""";

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    [Fact]
    public async Task HandleSessionAsync_ShouldCloseTheVendorSessionAndComplete_WhenTheCallerHangsUpOnAHealthySession()
    {
        // Arrange: a vendor like the live one, which sends nothing more after its burst and closes
        // only in answer to a close from the client.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = CreateBridge(fakeOpenAi);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up once both loops run, and nothing more reaches the bridge
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();

            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            var clientClose = await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            using (new AssertionScope())
            {
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                clientClose.Should().Be(
                    WebSocketCloseStatus.NormalClosure,
                    "the bridge closes toward the vendor with a normal closure when the caller hangs up");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "a session the caller ended and the vendor closed in answer is a completion");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(
                    0, "the session ended, so its duration is recorded");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldFailWithTheTransportError_WhenTheVendorDropsTheConnectionInAnswerToTheClose()
    {
        // Arrange: a vendor that holds the socket and never answers a close, so the test chooses what
        // happens once the bridge's close arrives. Abort needs this hold: without it the fake has
        // already closed by the time the test could drop the connection.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = CreateBridge(fakeOpenAi);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up once both loops run; the vendor drops the TCP connection, with
            // no close frame, the moment the bridge's close reaches it
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();

            await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            fakeOpenAi.Abort();

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                fault.Should().BeOfType<WebSocketException>(
                    "a transport that dies is not a clean ending, even under a close the bridge started");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "a connection dropped instead of closed is what this counter is for");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    0, "the vendor never answered the close, so nothing completed the handshake");
            }

            ((WebSocketException)fault!).WebSocketErrorCode.Should().Be(
                WebSocketError.ConnectionClosedPrematurely,
                "the socket read end-of-stream where the vendor's answering close should have been");
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
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

    private static OpenAiRealtimeBridge CreateBridge(RealtimeFakeServer fakeOpenAi)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        return new OpenAiRealtimeBridge(
            options,
            new RealtimeFunctionRegistry([]),
            NullLogger<OpenAiRealtimeBridge>.Instance)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
        };
    }
}

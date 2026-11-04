using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A <c>ControlMessages</c> subscriber that throws: its failure stays its own, and it is reported as its own. Every test
/// drives the real session over a real loopback socket, whose far end is a <see cref="RawWebSocketPeer"/> that sends
/// captured chan_websocket control messages.
/// </summary>
/// <remarks>
/// The report is counted on <c>audio.control_messages.subscriber_faults</c>, which is process-wide and has no tags: the
/// class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class WebSocketControlMessageGuardTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a disposal may take to release the connection: the closing handshake's 1 s bound and slack.</summary>
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(3);

    private const string MeterName = "Verbara.Sdk.Ari.Audio";
    private const string SubscriberFaults = "audio.control_messages.subscriber_faults";
    private const int AudioFrameBytes = 640;

    private static readonly ChanWebSocketCapturedMessage CapturedDtmf = ChanWebSocketWireCapture.ControlMessages.First(
        m => m.Event == "DTMF_END" && m.Format == ChanWebSocketCaptureFormat.PlainText);

    private static readonly ChanWebSocketCapturedMessage CapturedXoff = ChanWebSocketWireCapture.ControlMessages.First(
        m => m.Event == "MEDIA_XOFF" && m.Format == ChanWebSocketCaptureFormat.PlainText);

    // ============================================================ the failure stays its own

    [Fact]
    public async Task ControlMessages_ShouldReachTheOtherSubscriberAndKeepReading_WhenTheFirstSubscriberThrows()
    {
        // Arrange — two subscribers, the first of which throws
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await GuardedConnection.OpenAsync(bound.Token);
        var states = new ConcurrentQueue<AudioStreamState>();
        using var stateSubscription = connection.Session.StateChanges.Subscribe(states.Enqueue);
        using var throwing = connection.Session.ControlMessages.Subscribe(_ => throw new InvalidOperationException("consumer fault"));
        var second = new ConcurrentQueue<ChanWebSocketControlMessage>();
        using var other = connection.Session.ControlMessages.Subscribe(second.Enqueue);

        // Act — the captured DTMF, then an audio frame the consumer reads
        await connection.Peer.SendTextAsync(CapturedDtmf.Text);
        await connection.Peer.SendBinaryAsync(new byte[AudioFrameBytes]);
        var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope(CapturedDtmf.Source))
        {
            second.Should().ContainSingle("the second subscriber receives the message the first one threw on")
                .Which.Should().BeOfType<ChanWebSocketDtmf>();
            states.Should().Equal([AudioStreamState.Connected], "a subscriber's failure publishes nothing on StateChanges");
            frame.Length.Should().Be(AudioFrameBytes, "the read pump goes on and reads the next frame");
        }
    }

    [Fact]
    public async Task ControlMessages_ShouldDeliverTheNextMessage_WhenTheSameSubscriberThrewOnTheLastOne()
    {
        // Arrange — one subscriber, subscribed directly, that throws on the first message only
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await GuardedConnection.OpenAsync(bound.Token);
        var received = new ConcurrentQueue<ChanWebSocketControlMessage>();
        using var subscription = connection.Session.ControlMessages.Subscribe(message =>
        {
            received.Enqueue(message);
            if (received.Count == 1)
                throw new InvalidOperationException("consumer fault on the first message");
        });

        // Act
        await connection.Peer.SendTextAsync(CapturedDtmf.Text);
        await connection.Peer.SendTextAsync(CapturedXoff.Text);
        await connection.Peer.SendBinaryAsync(new byte[AudioFrameBytes]);
        var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            received.Select(m => m.GetType()).Should().Equal(
                [typeof(ChanWebSocketDtmf), typeof(ChanWebSocketMediaXoff)],
                "the subscriber that threw stays subscribed and receives the next message");
            frame.Length.Should().Be(AudioFrameBytes, "the read pump goes on");
        }
    }

    [Fact]
    public async Task ControlMessages_ShouldEndOnlyTheComposedSubscription_WhenItsOperatorHandlerThrows()
    {
        // Arrange — a subscription composed through an Rx operator whose handler throws, and one subscriber behind it
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await GuardedConnection.OpenAsync(bound.Token);
        var states = new ConcurrentQueue<AudioStreamState>();
        using var stateSubscription = connection.Session.StateChanges.Subscribe(states.Enqueue);
        var composedCalls = 0;
        using var composed = connection.Session.ControlMessages
            .Where(_ => true)
            .Subscribe(_ =>
            {
                Interlocked.Increment(ref composedCalls);
                throw new InvalidOperationException("consumer fault behind an operator");
            });
        var behind = new ConcurrentQueue<ChanWebSocketControlMessage>();
        using var other = connection.Session.ControlMessages.Subscribe(behind.Enqueue);

        // Act — two captured messages, then an audio frame
        await connection.Peer.SendTextAsync(CapturedDtmf.Text);
        await connection.Peer.SendTextAsync(CapturedXoff.Text);
        await connection.Peer.SendBinaryAsync(new byte[AudioFrameBytes]);
        var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            Volatile.Read(ref composedCalls).Should().Be(1, "the operator ends the composed subscription on its first failure");
            behind.Select(m => m.GetType()).Should().Equal(
                [typeof(ChanWebSocketDtmf), typeof(ChanWebSocketMediaXoff)],
                "the subscriber behind it receives every message");
            states.Should().Equal([AudioStreamState.Connected], "the stream stays live");
            frame.Length.Should().Be(AudioFrameBytes, "the read pump goes on");
        }
    }

    // ============================================================ the failure is reported as its own

    [Fact]
    public async Task ControlMessages_ShouldWriteOneWarningAndCountOneFault_WhenASubscriberThrowsOnAMessage()
    {
        // Arrange
        using var faults = new TransportFailureCounter(MeterName, SubscriberFaults);
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await GuardedConnection.OpenAsync(bound.Token);
        var thrown = new InvalidOperationException("consumer fault");
        using var throwing = connection.Session.ControlMessages.Subscribe(_ => throw thrown);

        // Act
        await connection.Peer.SendTextAsync(CapturedDtmf.Text);
        await connection.Peer.SendBinaryAsync(new byte[AudioFrameBytes]);
        var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            var warnings = connection.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
            warnings.Should().ContainSingle("each failure is written once");
            var warning = warnings.Count == 1 ? warnings[0] : null;
            warning?.Exception.Should().BeSameAs(thrown, "the Warning carries the subscriber's exception");
            warning?.Message.Should().Contain(nameof(ChanWebSocketDtmf), "the Warning names the message's type");
            faults.Total.Should().Be(1, "each failure is counted once on audio.control_messages.subscriber_faults");
            faults.Measurements.Should().Be(1);
            frame.Length.Should().Be(AudioFrameBytes, "the exception goes no further than its report: the pump goes on");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReturnReleaseAndReportOnce_WhenASubscriberThrowsOnTheCompletion()
    {
        // Arrange — a subscriber whose OnCompleted throws; the peer answers the disposal's close (GuardedConnection)
        using var faults = new TransportFailureCounter(MeterName, SubscriberFaults);
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await GuardedConnection.OpenAsync(bound.Token);
        var states = new ConcurrentQueue<AudioStreamState>();
        using var stateSubscription = connection.Session.StateChanges.Subscribe(states.Enqueue);
        var thrown = new InvalidOperationException("consumer fault on completion");
        using var throwing = connection.Session.ControlMessages.Subscribe(Observer.Create<ChanWebSocketControlMessage>(
            _ => { },
            () => throw thrown));

        // Act
        var disposal = await Record.ExceptionAsync(() => connection.Session.DisposeAsync().AsTask().WaitAsync(bound.Token));
        var released = await EndsWithinAsync(connection.Peer.Ended, ReleaseWait);

        // Assert
        using (new AssertionScope())
        {
            disposal.Should().BeNull("an exception on the completion does not stop the disposal");
            released.Should().BeTrue("the disposal releases the connection");
            var warnings = connection.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
            warnings.Should().ContainSingle("the failure on the completion is written once");
            (warnings.Count == 1 ? warnings[0].Exception : null).Should().BeSameAs(thrown, "the Warning carries the subscriber's exception");
            faults.Total.Should().Be(1, "the failure on the completion is counted once");
            states.Should().Equal([AudioStreamState.Connected, AudioStreamState.Disconnected], "a disposal ends the stream without Error");
        }
    }

    /// <summary>Whether <paramref name="ended"/> completes within <paramref name="wait"/>; running out is the failure asserted.</summary>
    private static async Task<bool> EndsWithinAsync(Task ended, TimeSpan wait)
    {
        try
        {
            await ended.WaitAsync(wait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// A started session over a real loopback socket, upgraded by the server's own upgrade code, whose far end is a
    /// <see cref="RawWebSocketPeer"/> that answers the session's close. No server owns the session, so only its own
    /// disposal releases the connection.
    /// </summary>
    private sealed class GuardedConnection : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _accepted;

        private GuardedConnection(TcpListener listener, TcpClient accepted, RawWebSocketPeer peer, WebSocketAudioSession session, CapturingLogger<WebSocketAudioSession> logger)
        {
            _listener = listener;
            _accepted = accepted;
            Peer = peer;
            Session = session;
            Logger = logger;
        }

        public RawWebSocketPeer Peer { get; }

        public WebSocketAudioSession Session { get; }

        public CapturingLogger<WebSocketAudioSession> Logger { get; }

        public static async Task<GuardedConnection> OpenAsync(CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync(token);
            var peer = await RawWebSocketPeer.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, token);
            var accepted = await accept;
            var stream = accepted.GetStream();
            var id = $"guard-{Guid.NewGuid():N}";
            var upgrade = peer.UpgradeAsync(id, token);
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, token);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, token);
            await upgrade;
            var logger = new CapturingLogger<WebSocketAudioSession>();
            var session = new WebSocketAudioSession(
                WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true }),
                id,
                "slin16",
                logger);
            peer.AnswerCloseWhenReceived();
            session.Start();
            return new GuardedConnection(listener, accepted, peer, session, logger);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _accepted.Dispose();
            Peer.Dispose();
            _listener.Dispose();
        }
    }
}

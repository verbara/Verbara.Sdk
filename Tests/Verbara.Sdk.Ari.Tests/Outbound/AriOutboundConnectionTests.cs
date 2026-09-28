using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Outbound;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Outbound;

public sealed class AriOutboundConnectionTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // How long a disconnect waits for Asterisk to answer its close, as the requirement states it.
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(5);

    // The listener's idle timeout, far longer than the close bound: before the bound, it was the only
    // thing that ended a close Asterisk never answered.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task DisconnectAsync_ShouldReturnWithinTheCloseBound_WhenAsteriskNeverAnswersTheClose()
    {
        // Asterisk never reads, so the close frame the connection sends is never answered. Before the
        // bound the call waited until the listener's idle timeout aborted the socket: five minutes here.
        // The listener's clock is fake too, and never moved: only the connection's own clock bounds its close.
        var listenerClock = new FakeTimeProvider();
        var connectionClock = new FakeTimeProvider();
        await using var session = await OutboundSession.OpenAsync(listenerClock);
        var connection = session.Connection;
        connection.TimeProvider = connectionClock;

        var disconnect = connection.DisconnectAsync().AsTask();

        // The bound is on the clock before the close frame goes out: read what it was armed with, then
        // run it out.
        var armed = connectionClock.TimersCreated.TryRead(out var bound) ? bound.DueTime : (TimeSpan?)null;
        connectionClock.Advance(CloseBound);
        await disconnect.WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            armed.Should().Be(CloseBound, "the wait for Asterisk's answer is bounded at five seconds, on the connection's clock");
            connection.IsConnected.Should().BeFalse("a close the far end never answers ends at the bound, as a disconnect");
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldNotWaitForTheCloseBound_WhenAsteriskAnswersTheClose()
    {
        // An Asterisk that answers the close ends the wait by itself: neither clock is moved.
        var listenerClock = new FakeTimeProvider();
        var connectionClock = new FakeTimeProvider();
        await using var session = await OutboundSession.OpenAsync(listenerClock);
        var connection = session.Connection;
        connection.TimeProvider = connectionClock;

        var answered = session.AnswerCloseAsync();
        await connection.DisconnectAsync().AsTask().WaitAsync(SignalTimeout);
        await answered.WaitAsync(SignalTimeout);

        connection.IsConnected.Should().BeFalse("the far end answered the close");
    }

    [Fact]
    public async Task DisconnectAsync_ShouldStopWaitingForTheClose_WhenTheCallerCancelsItsToken()
    {
        // The bound joins the caller's token, it does not replace it: a caller that cancels while
        // Asterisk has not answered still ends the wait at once, with neither clock moved.
        var listenerClock = new FakeTimeProvider();
        var connectionClock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        await using var session = await OutboundSession.OpenAsync(listenerClock);
        var connection = session.Connection;
        connection.TimeProvider = connectionClock;

        var disconnect = connection.DisconnectAsync(caller.Token).AsTask();
        await caller.CancelAsync();
        await disconnect.WaitAsync(SignalTimeout);

        connection.IsConnected.Should().BeFalse("a caller that stops waiting for the close still gets an ordinary disconnect");
    }

    /// <summary>
    /// A listener on a fake clock, with <see cref="IdleTimeout"/> as its idle timeout, and the Asterisk that
    /// dialled in to it. Opening it waits until the listener's read pump has delivered one event from
    /// that Asterisk, so the connection is being served before the test disconnects it. Asterisk reads
    /// nothing unless <see cref="AnswerCloseAsync"/> is called.
    /// </summary>
    private sealed class OutboundSession : IAsyncDisposable
    {
        private readonly AriOutboundListener _listener;
        private readonly ClientWebSocket _asterisk;

        private OutboundSession(AriOutboundListener listener, ClientWebSocket asterisk, AriOutboundConnection connection)
        {
            _listener = listener;
            _asterisk = asterisk;
            Connection = connection;
        }

        public AriOutboundConnection Connection { get; }

        public static async Task<OutboundSession> OpenAsync(TimeProvider listenerClock)
        {
            var listener = new AriOutboundListener(
                Options.Create(new AriOutboundListenerOptions
                {
                    ListenAddress = "127.0.0.1",
                    Port = 0,
                    Path = "/ari/events",
                    ConnectionIdleTimeout = IdleTimeout,
                }),
                NullLogger<AriOutboundListener>.Instance,
                listenerClock);
            var asterisk = new ClientWebSocket();
            var opened = false;
            try
            {
                await listener.StartAsync();

                var accepted = new TaskCompletionSource<AriOutboundConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var onAccepted = listener.OnConnectionAccepted.Subscribe(c => accepted.TrySetResult(c));
                await asterisk.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{listener.BoundPort}/ari/events?app=myapp"), CancellationToken.None);
                var connection = await accepted.Task.WaitAsync(SignalTimeout);

                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var onEvent = connection.Events.Subscribe(_ => delivered.TrySetResult());
                await asterisk.SendAsync(
                    Encoding.UTF8.GetBytes("""{"type":"DeviceStateChanged","application":"myapp"}"""),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
                await delivered.Task.WaitAsync(SignalTimeout);

                opened = true;
                return new OutboundSession(listener, asterisk, connection);
            }
            finally
            {
                // A session that never opened has no owner to dispose what it started: release it here,
                // and the failure still reaches the test.
                if (!opened)
                {
                    asterisk.Dispose();
                    await listener.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Reads until the connection's close frame arrives, then answers it. Completes once the answer
        /// has been sent.
        /// </summary>
        public async Task AnswerCloseAsync()
        {
            var buffer = new byte[1024];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _asterisk.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            }
            while (result.MessageType != WebSocketMessageType.Close);

            await _asterisk.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();
            _asterisk.Dispose();
        }
    }
}

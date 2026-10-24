using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Enums;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientClosingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly FieldInfo SocketField = typeof(AriClient).GetField("_webSocket", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ShouldLeaveStateDisconnected_WhenReconnecting(bool heldUpgrade)
    {
        await using var peer = new ClosingPeer { HoldReconnectUpgrade = heldUpgrade };
        var clock = new FakeTimeProvider();
        var log = new ClosingLogger<AriClient>();
        var sut = Create(peer.Port, log, true, heldUpgrade ? TimeSpan.Zero : TimeSpan.FromMinutes(1));
        sut.TimeProvider = clock;
        await sut.ConnectAsync();
        while (clock.TimersCreated.TryRead(out _)) { }
        var first = await peer.NextAsync();
        first.Drop();
        if (heldUpgrade) await peer.NextAsync();
        else await log.Reconnecting.Task.WaitAsync(Limit);
        await sut.DisposeAsync().AsTask().WaitAsync(Limit);
        using (new AssertionScope())
        {
            sut.EventLoop!.IsCompleted.Should().BeTrue();
            sut.State.Should().Be(AriConnectionState.Disconnected, "disposal ended the reconnect loop");
            peer.Dials.Should().Be(heldUpgrade ? 2 : 1);
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldLeaveStateDisconnected_WhenAReconnectUpgradeIsHeld()
    {
        await using var peer = new ClosingPeer { HoldReconnectUpgrade = true };
        var sut = Create(peer.Port, new ClosingLogger<AriClient>(), true);
        try
        {
            await sut.ConnectAsync();
            (await peer.NextAsync()).Drop();
            await peer.NextAsync();
            await sut.DisconnectAsync().AsTask().WaitAsync(Limit);
            sut.State.Should().Be(AriConnectionState.Disconnected);
            sut.EventLoop!.IsCompleted.Should().BeTrue();
            peer.Dials.Should().Be(2);
        }
        finally { await sut.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisconnectAsync_ShouldSendOneNormalClose_WhenReceiving(bool cancelledToken, bool eventsBeforeAnswer)
    {
        await using var peer = new ClosingPeer { EventsBeforeAnswer = eventsBeforeAnswer, AnswerClose = !cancelledToken };
        using var receive = new ReceiveStartSignal();
        var sut = Create(peer.Port, new ClosingLogger<AriClient>());
        try
        {
            await receive.Watch(() => sut.ConnectAsync().AsTask());
            var connection = await peer.NextAsync();
            await receive.Started.WaitAsync(Limit);
            var socket = (ClientWebSocket)SocketField.GetValue(sut)!;
            using var caller = new CancellationTokenSource();
            if (cancelledToken) caller.Cancel();
            await sut.DisconnectAsync(caller.Token).AsTask().WaitAsync(Limit);
            var sawClose = await connection.Ended.WaitAsync(Limit);
            using (new AssertionScope())
            {
                sawClose.Should().BeTrue("the far end must receive a close frame, not an aborted connection");
                connection.CloseCount.Should().Be(1);
                connection.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
                if (!cancelledToken) socket.State.Should().Be(WebSocketState.Closed, "events preceding the answer must not stop its sole reader");
                sut.State.Should().Be(AriConnectionState.Disconnected);
            }
        }
        finally { await sut.DisposeAsync(); }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldNotReconnect_WhenThePeerAnswersAndAutoReconnectIsEnabled()
    {
        await using var peer = new ClosingPeer();
        var sut = Create(peer.Port, new ClosingLogger<AriClient>(), true);
        try
        {
            await sut.ConnectAsync();
            await peer.NextAsync();
            await sut.DisconnectAsync().AsTask().WaitAsync(Limit);
            await sut.EventLoop!.WaitAsync(Limit);
            peer.Dials.Should().Be(1, "a requested ending suppresses reconnect");
            sut.State.Should().Be(AriConnectionState.Disconnected);
        }
        finally { await sut.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventsLoop_ShouldAnswerPeerCloseBeforeRelease_WhenAutoReconnectIsEnabledOrDisabled(bool autoReconnect)
    {
        await using var peer = new ClosingPeer();
        var log = new ClosingLogger<AriClient>();
        var sut = Create(peer.Port, log, autoReconnect);
        try
        {
            await sut.ConnectAsync();
            var first = await peer.NextAsync();
            await first.CloseAsync();
            if (autoReconnect) await peer.NextAsync();
            else await sut.EventLoop!.WaitAsync(Limit);
            // For reconnect-off, the socket remains retained today; the loop has ended but sends no
            // answer. Its completion is the causal failure signal, not a peer receive timeout.
            if (autoReconnect) await first.Ended.WaitAsync(Limit);
            using (new AssertionScope())
            {
                first.CloseCount.Should().Be(1, "an Asterisk-initiated close is answered before release/redial");
                if (autoReconnect)
                {
                    first.CloseOrder.Should().BeGreaterThan(0).And.BeLessThan(peer.SecondDialOrder);
                    peer.Dials.Should().Be(2, "the existing policy permits exactly one successful reconnect");
                }
                else peer.Dials.Should().Be(1, "automatic reconnect is disabled");
            }
        }
        finally { await sut.DisposeAsync(); }
    }

    [Fact]
    public async Task EventsLoop_ShouldLeaveStateDisconnected_WhenPeerClosesWithAutoReconnectDisabled()
    {
        await using var peer = new ClosingPeer();
        var sut = Create(peer.Port, new ClosingLogger<AriClient>());
        try
        {
            await sut.ConnectAsync();
            await (await peer.NextAsync()).CloseAsync();
            await sut.EventLoop!.WaitAsync(Limit);
            sut.State.Should().Be(AriConnectionState.Disconnected, "the peer close ended the events loop without caller cleanup");
            peer.Dials.Should().Be(1);
        }
        finally { await sut.DisposeAsync(); }
    }

    [Fact]
    public async Task EventsLoop_ShouldNotLogError_WhenDisconnectLandsInsideAPartialRead()
    {
        await using var peer = new ClosingPeer();
        using var receive = new WebSocketReceiveHook();
        var log = new ClosingLogger<AriClient>();
        var sut = Create(peer.Port, log);
        Task? disconnect = null;
        try
        {
            await receive.Watch(() => sut.ConnectAsync().AsTask());
            var connection = await peer.NextAsync();
            receive.Arm(() =>
            {
                var source = (CancellationTokenSource)typeof(AriClient).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sut)!;
                source.Cancel();
                disconnect = sut.DisconnectAsync().AsTask();
            });
            await connection.SendAsync("{\"type\":\"StasisStart\",", end: false);
            await receive.Fired.WaitAsync(Limit);
            await disconnect!.WaitAsync(Limit);
            log.Entries.Should().NotContain(e => e.Level == LogLevel.Error, "the reader's requested disconnect caused the socket-state failure");
        }
        finally { await sut.DisposeAsync(); }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldWaitForItsBound_WhenThePeerNeverAnswers()
    {
        await using var peer = new ClosingPeer { AnswerClose = false };
        using var receive = new ReceiveStartSignal();
        var sut = Create(peer.Port, new ClosingLogger<AriClient>());
        var clock = new FakeTimeProvider();
        sut.TimeProvider = clock;
        try
        {
            await receive.Watch(() => sut.ConnectAsync().AsTask());
            var first = await peer.NextAsync();
            await receive.Started.WaitAsync(Limit);
            while (clock.TimersCreated.TryRead(out _)) { }
            var disconnect = sut.DisconnectAsync().AsTask();
            var sawClose = await first.Ended.WaitAsync(Limit);
            var returnedBeforeBound = disconnect.IsCompleted;
            clock.Advance(TimeSpan.FromSeconds(5));
            await disconnect.WaitAsync(Limit);
            using (new AssertionScope())
            {
                sawClose.Should().BeTrue("the peer must see the frame even when it never answers");
                returnedBeforeBound.Should().BeFalse("an unanswered close waits for the existing bound");
                sut.State.Should().Be(AriConnectionState.Disconnected);
            }
        }
        finally { await sut.DisposeAsync(); }
    }

    internal static AriClient Create(int port, ILogger<AriClient> log, bool reconnect = false, TimeSpan? delay = null) => new(
        Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}", Username = "test", Password = "test", Application = "test-app",
            AutoReconnect = reconnect, ReconnectInitialDelay = delay ?? TimeSpan.Zero, ReconnectMaxDelay = delay ?? TimeSpan.Zero
        }), log);
}

internal sealed class ClosingLogger<T> : ILogger<T>
{
    internal ConcurrentQueue<(LogLevel Level, EventId Id, Exception? Exception, string Message)> Entries { get; } = new();
    internal TaskCompletionSource Reconnecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue((logLevel, eventId, exception, formatter(state, exception)));
        if (eventId.Name == "ConnectionClosed") Closed.TrySetResult();
        if (eventId.Name == "Reconnecting") Reconnecting.TrySetResult();
    }
}

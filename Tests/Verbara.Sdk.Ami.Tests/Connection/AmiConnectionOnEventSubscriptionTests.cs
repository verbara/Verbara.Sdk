using System.Globalization;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <c>AmiConnection.OnEvent</c>'s <c>add</c> and <c>remove</c> keep the meaning of the field-like multicast event they
/// replaced: a removed handler is called no more, removing a handler subscribed twice removes its last subscription, and
/// a <see langword="null"/> handler, or the removal of one never subscribed, changes nothing.
/// </summary>
/// <remarks>
/// Deliveries are counted, never timed: the peer writes a sentinel event last, and a handler that sees it has seen every
/// event before it. Time enters only as <see cref="Bound"/>, a hang bound.
/// </remarks>
public sealed class AmiConnectionOnEventSubscriptionTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const int Events = 3;

    private const string Sentinel = "SIP/sentinel";

    /// <summary>The handlers called for one event when "twice" was subscribed, then "other", then "twice" again.</summary>
    private static readonly string[] TwiceThenOther = ["twice", "other"];

    [Fact]
    public async Task OnEvent_ShouldStopCallingAHandler_WhenItIsRemoved()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new ListLogger());
        var calls = new CallLog();
        var removed = calls.Handler("removed");
        var kept = calls.Handler("kept");
        connection.OnEvent += removed;
        connection.OnEvent += kept;
        connection.OnEvent -= removed;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WriteEventsAsync(socket);
        var reached = await CompletesWithinBoundAsync(calls.SentinelReached("kept"));

        using (new AssertionScope())
        {
            reached.Should().BeTrue("the handler still subscribed receives every event up to the sentinel");
            calls.Names().Should().Equal(Enumerable.Repeat("kept", Events),
                "a removed handler is called no more, and the other one is called once per event");
        }
    }

    [Fact]
    public async Task OnEvent_ShouldRemoveTheLastSubscription_WhenAHandlerSubscribedTwiceIsRemovedOnce()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new ListLogger());
        var calls = new CallLog();
        var twice = calls.Handler("twice");
        var other = calls.Handler("other");
        connection.OnEvent += twice;
        connection.OnEvent += other;
        connection.OnEvent += twice;
        connection.OnEvent -= twice;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WriteEventsAsync(socket);
        var reached = await CompletesWithinBoundAsync(calls.SentinelReached("other"));

        using (new AssertionScope())
        {
            reached.Should().BeTrue("both handlers receive every event up to the sentinel");
            calls.Names().Should().Equal(
                Enumerable.Range(0, Events).SelectMany(_ => TwiceThenOther),
                "removing a handler subscribed twice removes its last subscription, as a multicast delegate's " +
                "removal does: the first one stays, ahead of the handler subscribed after it");
        }
    }

    [Fact]
    public async Task OnEvent_ShouldChangeNothing_WhenANullOrUnsubscribedHandlerIsAddedOrRemoved()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new ListLogger();
        await using var connection = Create(factory, logger);
        var calls = new CallLog();
        var subscribed = calls.Handler("subscribed");
        var neverSubscribed = calls.Handler("never");
        connection.OnEvent += subscribed;
        connection.OnEvent += null;
        connection.OnEvent -= null;
        connection.OnEvent -= neverSubscribed;
        var socket = await ConnectAsync(connection, factory, peerCts);

        await WriteEventsAsync(socket);
        var reached = await CompletesWithinBoundAsync(calls.SentinelReached("subscribed"));

        using (new AssertionScope())
        {
            reached.Should().BeTrue("the subscribed handler receives every event up to the sentinel");
            calls.Names().Should().Equal(Enumerable.Repeat("subscribed", Events),
                "only the subscribed handler is called, once per event");
            logger.Formats().Should().NotContain("[AMI_EVENT] OnEvent handler threw on {EventType}",
                "a null handler is never subscribed, so no dispatch calls it and fails");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static async Task WriteEventsAsync(PipedSocket socket)
    {
        for (var i = 0; i < Events; i++)
        {
            (await socket.WriteEventAsync("PeerStatus", [new("Peer", "SIP/" + i.ToString(CultureInfo.InvariantCulture))]))
                .Should().BeTrue();
        }

        (await socket.WriteEventAsync("PeerStatus", [new("Peer", Sentinel)])).Should().BeTrue();
    }

    private static AmiConnection Create(PipedSocketFactory factory, ILogger<AmiConnection> logger) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        }), factory, logger);

    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records, in call order, the name of every handler called for an event before the sentinel; a handler that sees
    /// the sentinel completes its signal.
    /// </summary>
    private sealed class CallLog
    {
        private readonly Lock _gate = new();
        private readonly List<string> _names = [];
        private readonly Dictionary<string, TaskCompletionSource> _sentinels = [];

        public Func<ManagerEvent, ValueTask> Handler(string name)
        {
            lock (_gate)
            {
                _sentinels[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return evt =>
            {
                lock (_gate)
                {
                    if (evt is PeerStatusEvent { Peer: Sentinel })
                        _sentinels[name].TrySetResult();
                    else
                        _names.Add(name);
                }

                return ValueTask.CompletedTask;
            };
        }

        public Task SentinelReached(string name)
        {
            lock (_gate)
            {
                return _sentinels[name].Task;
            }
        }

        public IReadOnlyList<string> Names()
        {
            lock (_gate)
            {
                return [.. _names];
            }
        }
    }

    /// <summary>A logger that keeps the message format of every entry.</summary>
    private sealed class ListLogger : ILogger<AmiConnection>
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _formats = [];

        public IReadOnlyList<string?> Formats()
        {
            lock (_gate)
            {
                return [.. _formats];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string? format = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                && pairs.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value is string original)
            {
                format = original;
            }
            lock (_gate)
            {
                _formats.Add(format);
            }
        }
    }
}

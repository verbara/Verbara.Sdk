using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An AMI message that carries an <c>Event</c> header is an event, even when it also carries a <c>Response</c> header.
/// Asterisk 20.20.1, 22.9.0 and 23.4.1 send two such events of their own — <c>OriginateResponse</c>, whose
/// <c>Response</c> is the originate's outcome and whose <c>ActionID</c> is the originate's, and the security event
/// <c>ChallengeResponseFailed</c>, whose <c>Response</c> is the digest the client sent — and a <c>UserEvent</c> carries
/// whatever headers its sender gave it. Every response Asterisk sends starts with <c>Response</c> and carries no
/// <c>Event</c> header. The connection asked "is it a response?" first, so it took each of these events for a second
/// response and dropped it: no collector, no <c>OnEvent</c> handler and no observer ever saw it.
/// </summary>
/// <remarks>
/// The peer is an in-memory <see cref="PipedSocket"/> that writes the frames Asterisk wrote in the raw captures, header
/// for header. A delivery is asserted against a sentinel event the peer writes after the frame under test: events
/// reach handlers and observers in the order the connection read them, so once the sentinel has arrived, a frame that
/// has not will never arrive. Time enters only as <see cref="Bound"/>, a hang bound, and as
/// <see cref="EventTimeout"/>, the connection's own bound on an event-generating action.
/// </remarks>
public sealed class AmiConnectionEventWithResponseHeaderTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The connection's <c>DefaultEventTimeout</c>: short, so a red ends fast, and far above what a frame written at
    /// once takes to arrive.
    /// </summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);

    private const string SentinelPeer = "SIP/sentinel";

    // ── The originate's collector ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldOriginateResponse_WhenTheEventCarriesAResponseHeader()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(NewOriginate()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await AnswerOriginateAsync(peer, id, "Success")).Should().BeTrue("the peer answers the originate as Asterisk does");
        var result = await ended.WaitAsync(Bound);

        var originate = result.Events.OfType<OriginateResponseEvent>().ToList();
        using (new AssertionScope())
        {
            originate.Should().ContainSingle(
                $"Asterisk sent one OriginateResponse for ActionID {id}; the enumeration ended with {Describe(result.Error)}");
            originate.FirstOrDefault()?.Response.Should().Be("Success", "its Response header is the originate's outcome");
            originate.FirstOrDefault()?.Channel.Should().Be(OriginatedChannel);
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldEndAfterOriginateResponse_WhenTheOriginateIsAsync()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(NewOriginate()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await AnswerOriginateAsync(peer, id, "Failure")).Should().BeTrue("the peer answers the originate as Asterisk does");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull(
                "an async originate's sequence is complete once its one OriginateResponse has arrived, so it ends " +
                $"without waiting for the {EventTimeout.TotalSeconds:0} s event timeout");
            result.Events.OfType<OriginateResponseEvent>().Select(e => e.Response).Should().Equal(["Failure"],
                "the OriginateResponse is the sequence's payload, not a marker to swallow");
        }
    }

    // ── OnEvent and the observers ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnEvent_ShouldReceiveOriginateResponse_WhenTheEventCarriesAResponseHeader()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var received = new Received();
        connection.OnEvent += evt =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        };
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(NewOriginate()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await AnswerOriginateAsync(peer, id, "Success")).Should().BeTrue("the peer answers the originate as Asterisk does");
        (await WriteSentinelAsync(peer)).Should().BeTrue("the peer sends an event after the OriginateResponse");
        (await CompletesWithinBoundAsync(received.Sentinel)).Should().BeTrue("the handler receives the sentinel");
        await ended.WaitAsync(Bound);

        received.TypesBeforeSentinel().Should().Equal(["OriginateResponse"],
            "OnEvent is raised for every event the connection read, in order, and the OriginateResponse came before the sentinel");
    }

    [Fact]
    public async Task Subscribe_ShouldReceiveOriginateResponse_WhenTheEventCarriesAResponseHeader()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var received = new Received();
        using var subscription = connection.Subscribe(received);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(NewOriginate()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await AnswerOriginateAsync(peer, id, "Success")).Should().BeTrue("the peer answers the originate as Asterisk does");
        (await WriteSentinelAsync(peer)).Should().BeTrue("the peer sends an event after the OriginateResponse");
        (await CompletesWithinBoundAsync(received.Sentinel)).Should().BeTrue("the observer receives the sentinel");
        await ended.WaitAsync(Bound);

        received.TypesBeforeSentinel().Should().Equal(["OriginateResponse"],
            "an observer receives every event the connection read, in order, and the OriginateResponse came before the sentinel");
    }

    /// <summary>
    /// <c>ChallengeResponseFailed</c> as Asterisk 22.9.0 wrote it for a failed MD5 login from another session, read by a
    /// user whose read classes include <c>security</c>: its <c>Response</c> is the client's digest, and it carries no
    /// <c>ActionID</c>, so no action can be waiting for it.
    /// </summary>
    [Fact]
    public async Task OnEvent_ShouldReceiveAnEventWithAResponseHeader_WhenNoActionIsPending()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var received = new Received();
        connection.OnEvent += evt =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        };
        var peer = await ConnectAsync(connection, factory, peerCts);

        (await peer.WriteEventAsync("ChallengeResponseFailed",
        [
            new("Privilege", "security,all"), new("EventTV", "2026-10-01T00:26:31.580+0000"),
            new("Severity", "Error"), new("Service", "AMI"), new("EventVersion", "1"), new("AccountID", ""),
            new("SessionID", "0x7f9f28003400"), new("LocalAddress", "IPV4/TCP/0.0.0.0/5038"),
            new("RemoteAddress", "IPV4/TCP/192.0.2.1/59718"), new("Challenge", "242277734"),
            new("Response", "2d3daa36429812247cf7c5bba4a1e6ff"), new("ExpectedResponse", "00000000000000000000000000000000"),
            new("SessionTV", "1970-01-01T00:00:00.000+0000"),
        ])).Should().BeTrue("the peer reports a failed MD5 login from another session");
        (await WriteSentinelAsync(peer)).Should().BeTrue("the peer sends an event after it");
        (await CompletesWithinBoundAsync(received.Sentinel)).Should().BeTrue("the handler receives the sentinel");

        using (new AssertionScope())
        {
            received.TypesBeforeSentinel().Should().Equal(["ChallengeResponseFailed"],
                "a security event is an event, whatever its Response header holds");
            received.BeforeSentinel().OfType<ChallengeResponseFailedEvent>().SingleOrDefault()?.Response
                .Should().Be("2d3daa36429812247cf7c5bba4a1e6ff");
        }
    }

    // ── A pending action's response ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The order a <c>SendActionAsync(Originate)</c> caller depends on. Asterisk wrote the originate's own
    /// <c>Response: Success</c> before its <c>OriginateResponse</c> in 240 of 240 measured runs, but nothing in the
    /// protocol orders them, and an <c>OriginateResponse</c> that arrived first was taken for the answer: the caller saw
    /// <c>Failure</c> for an originate Asterisk had accepted.
    /// </summary>
    [Fact]
    public async Task SendActionAsync_ShouldCompleteWithTheResponse_WhenAnOriginateResponseWithTheSameActionIdArrivesFirst()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var sent = connection.SendActionAsync(NewOriginate()).AsTask();
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteOriginateResponseAsync(peer, id, "Failure")).Should().BeTrue("the peer reports the originate's outcome first");
        (await peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]))
            .Should().BeTrue("then the peer answers the action");
        var response = await sent.WaitAsync(Bound);

        using (new AssertionScope())
        {
            response.Response.Should().Be("Success", "the action's answer is the message without an Event header");
            response.Message.Should().Be("Originate successfully queued");
        }
    }

    [Fact]
    public async Task SendActionAsync_ShouldCompleteWithTheResponse_WhenTheMessageHasNoEventHeader()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var received = new Received();
        connection.OnEvent += evt =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        };
        var peer = await ConnectAsync(connection, factory, peerCts);

        var sent = connection.SendActionAsync(NewOriginate()).AsTask();
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]))
            .Should().BeTrue("the peer answers the action");
        var response = await sent.WaitAsync(Bound);
        (await WriteSentinelAsync(peer)).Should().BeTrue("the peer sends an event after the response");
        (await CompletesWithinBoundAsync(received.Sentinel)).Should().BeTrue("the handler receives the sentinel");

        using (new AssertionScope())
        {
            response.Response.Should().Be("Success", "a message with Response and no Event header is the action's response");
            response.Message.Should().Be("Originate successfully queued");
            received.TypesBeforeSentinel().Should().BeEmpty("a response is not delivered as an event");
        }
    }

    /// <summary>
    /// The first action is answered; an event that carries its ActionID but no <c>Response</c> header arrives while a
    /// second action that reuses the ActionID is pending. Only the second action's own response completes it.
    /// </summary>
    [Fact]
    public async Task SendActionAsync_ShouldNotCompleteASecondAction_WhenAnUnrelatedEventCarriesTheSameActionId()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var peer = await ConnectAsync(connection, factory, peerCts);
        const string reused = "t3c-reused";

        var first = connection.SendActionAsync(new PingAction { ActionId = reused }).AsTask();
        (await ReadActionIdAsync(peer, peerCts)).Should().Be(reused);
        (await peer.RespondAsync("Success", reused, [new("Ping", "Pong")])).Should().BeTrue();
        var firstResponse = await first.WaitAsync(Bound);

        var second = connection.SendActionAsync(new PingAction { ActionId = reused }).AsTask();
        (await ReadActionIdAsync(peer, peerCts)).Should().Be(reused);
        (await peer.WriteEventAsync("UserEvent", [new("UserEvent", "late"), new("ActionID", reused)]))
            .Should().BeTrue("an event that carries the ActionID arrives before the second answer");
        (await peer.RespondAsync("Success", reused, [new("Ping", "Pong"), new("Message", "second")])).Should().BeTrue();
        var secondResponse = await second.WaitAsync(Bound);

        using (new AssertionScope())
        {
            firstResponse.Response.Should().Be("Success");
            secondResponse.Response.Should().Be("Success", "the event is not the second action's answer");
            secondResponse.Message.Should().Be("second", "the second action completes with its own response");
        }
    }

    // ── The counters ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An <c>OriginateResponse</c> is counted where it is delivered: in <c>ami.events.received</c>, not in
    /// <c>ami.responses.received</c>. Between two sentinels the peer writes the originate's response, its
    /// <c>OriginateResponse</c> and the second sentinel: one response and two events. The counters are process-wide, so
    /// only what this test's connection reads is counted: its reader loop runs in the execution context of the connect,
    /// which carries <see cref="CountedHere"/>.
    /// </summary>
    [Fact]
    public async Task ReaderLoop_ShouldCountOriginateResponseAsAnEvent_WhenTheEventCarriesAResponseHeader()
    {
        using var counts = new ReceivedCounts();
        CountedHere.Value = true;
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory);
        var before = new Received();
        var after = new Received(SecondSentinelPeer);
        connection.OnEvent += evt =>
        {
            before.Add(evt);
            after.Add(evt);
            return ValueTask.CompletedTask;
        };
        var peer = await ConnectAsync(connection, factory, peerCts);
        (await WriteSentinelAsync(peer)).Should().BeTrue("the peer sends the first sentinel");
        (await CompletesWithinBoundAsync(before.Sentinel)).Should().BeTrue("the handler receives the first sentinel");
        var (eventsBefore, responsesBefore) = counts.Read();

        var ended = ReadToEndAsync(connection.SendEventGeneratingActionAsync(NewOriginate()));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await AnswerOriginateAsync(peer, id, "Success")).Should().BeTrue("the peer answers the originate as Asterisk does");
        (await peer.WriteEventAsync("PeerStatus", [new("Peer", SecondSentinelPeer)]))
            .Should().BeTrue("the peer sends the second sentinel");
        (await CompletesWithinBoundAsync(after.Sentinel)).Should().BeTrue("the handler receives the second sentinel");
        var (eventsAfter, responsesAfter) = counts.Read();
        await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            (responsesAfter - responsesBefore).Should().Be(1,
                "ami.responses.received counts the originate's own response, and the OriginateResponse is not one");
            (eventsAfter - eventsBefore).Should().Be(2,
                "ami.events.received counts the OriginateResponse and the second sentinel");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private const string SecondSentinelPeer = "SIP/sentinel-2";

    /// <summary>Marks the execution context whose counter increments <see cref="ReceivedCounts"/> keeps.</summary>
    private static readonly AsyncLocal<bool> CountedHere = new();

    /// <summary>
    /// Sums <c>ami.events.received</c> and <c>ami.responses.received</c>, keeping only the increments made in an
    /// execution context that carries <see cref="CountedHere"/>.
    /// </summary>
    private sealed class ReceivedCounts : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private long _events;
        private long _responses;

        public ReceivedCounts()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is "ami.events.received" or "ami.responses.received")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (!CountedHere.Value)
                    return;

                if (instrument.Name == "ami.events.received")
                    Interlocked.Add(ref _events, measurement);
                else
                    Interlocked.Add(ref _responses, measurement);
            });
            _listener.Start();
        }

        public (long Events, long Responses) Read() =>
            (Interlocked.Read(ref _events), Interlocked.Read(ref _responses));

        public void Dispose() => _listener.Dispose();
    }

    private const string OriginatedChannel = "Local/s@hold-00000132;1";

    private static OriginateAction NewOriginate() => new()
    {
        Channel = "Local/s@hold",
        Context = "hold",
        Exten = "s",
        Priority = 1,
        Timeout = 30_000,
        IsAsync = true,
    };

    /// <summary>
    /// Answers an async originate as Asterisk 22.9.0 did: the action's <c>Response: Success</c>, then one
    /// <c>OriginateResponse</c> for its ActionID with <paramref name="outcome"/> as its <c>Response</c>.
    /// </summary>
    private static async Task<bool> AnswerOriginateAsync(PipedSocket peer, string id, string outcome) =>
        await peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")])
        & await WriteOriginateResponseAsync(peer, id, outcome);

    private static Task<bool> WriteOriginateResponseAsync(PipedSocket peer, string id, string outcome) =>
        peer.WriteEventAsync("OriginateResponse",
        [
            new("Privilege", "call,all"), new("ActionID", id), new("Response", outcome),
            new("Channel", OriginatedChannel), new("Context", "hold"), new("Exten", "s"),
            new("Reason", outcome == "Success" ? "4" : "0"), new("Uniqueid", "1790763823.612"),
            new("CallerIDNum", "<unknown>"), new("CallerIDName", "<unknown>"),
        ]);

    private static Task<bool> WriteSentinelAsync(PipedSocket peer) =>
        peer.WriteEventAsync("PeerStatus", [new("Peer", SentinelPeer)]);

    private static AmiConnection Create(PipedSocketFactory factory) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = EventTimeout,
        }), factory, NullLogger<AmiConnection>.Instance);

    /// <summary>Connects, with the next socket's peer completing the login; returns that socket.</summary>
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

    /// <summary>The ActionID of the next action the connection sends to <paramref name="peer"/>.</summary>
    private static async Task<string> ReadActionIdAsync(PipedSocket peer, CancellationTokenSource peerCts)
    {
        var action = await peer.ReadActionAsync(peerCts.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
        return PipedSocket.ActionIdOf(action);
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

    private static string Describe(Exception? error) => error is null ? "no error" : error.GetType().Name;

    /// <summary>
    /// Enumerates <paramref name="events"/> to its end and reports what the caller saw: every event, and the error
    /// the enumeration ended with, if any. It starts the enumeration, which sends the action, before it returns.
    /// </summary>
    private static async Task<Ended> ReadToEndAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        try
        {
            await foreach (var evt in events)
                received.Add(evt);

            return new Ended(received, Error: null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // How the enumeration ended is what the tests assert: an error is recorded, not rethrown.
            return new Ended(received, ex);
        }
    }

    private sealed record Ended(IReadOnlyList<ManagerEvent> Events, Exception? Error);

    /// <summary>
    /// Records every event delivered to it, as an <c>OnEvent</c> handler or as an observer, and completes
    /// <see cref="Sentinel"/> when the sentinel PeerStatus (by default, the one <see cref="WriteSentinelAsync"/> writes)
    /// arrives.
    /// </summary>
    private sealed class Received(string sentinelPeer = SentinelPeer) : IObserver<ManagerEvent>
    {
        private readonly Lock _gate = new();
        private readonly List<ManagerEvent> _events = [];
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Sentinel => _sentinel.Task;

        public void Add(ManagerEvent evt)
        {
            if (evt is PeerStatusEvent peerStatus && peerStatus.Peer == sentinelPeer)
            {
                _sentinel.TrySetResult();
                return;
            }

            lock (_gate)
            {
                if (!_sentinel.Task.IsCompleted)
                    _events.Add(evt);
            }
        }

        public IReadOnlyList<ManagerEvent> BeforeSentinel()
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }

        public IReadOnlyList<string?> TypesBeforeSentinel() => [.. BeforeSentinel().Select(e => e.EventType)];

        public void OnNext(ManagerEvent value) => Add(value);

        public void OnError(Exception error)
        {
            // The connection never reports one; nothing to observe.
        }

        public void OnCompleted()
        {
            // The connection never reports one; nothing to observe.
        }
    }
}

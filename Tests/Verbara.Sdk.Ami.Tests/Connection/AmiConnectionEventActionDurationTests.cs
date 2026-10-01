using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The internal <c>SendEventGeneratingActionAsync</c> overload that takes how long Asterisk may take to end the action's
/// sequence. Verbara.Sdk.Live passes an originate's own <c>Timeout</c> there: Asterisk lets the destination ring that
/// long before it reports the outcome in an <c>OriginateResponse</c> (8.0 s for a destination that answers after a
/// dialplan <c>Wait(8)</c>, 12.0 s for one that never answers within a 12 s <c>Timeout</c>, measured on 20.20.1, 22.9.0
/// and 23.4.1), so the connection's <c>DefaultEventTimeout</c>, 5 s by default, ended every such originate in an
/// <see cref="OperationCanceledException"/>. The overload bounds the wait by that duration plus
/// <c>DefaultResponseTimeout</c> instead of by <c>DefaultEventTimeout</c>; its collector is fed by the reader loop, so
/// the outcome cannot be dropped behind the event pump.
/// </summary>
/// <remarks>
/// No test sleeps. The late-outcome test learns that the event timeout has run out from a second enumeration, of the
/// public overload, started after the originate's and ended by that same timeout: once it has thrown, the originate's
/// wait has outlived <c>DefaultEventTimeout</c> too. <see cref="Bound"/> is a hang bound.
/// </remarks>
public sealed class AmiConnectionEventActionDurationTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string OriginatedChannel = "Local/s@wait8-00000132;1";

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldTheOutcome_WhenItArrivesAfterTheEventTimeoutButWithinTheGivenDuration()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory,
            eventTimeout: TimeSpan.FromMilliseconds(250), responseTimeout: TimeSpan.FromMinutes(1));
        var peer = await ConnectAsync(connection, factory, peerCts);

        var originate = ReadToEndAsync(connection.SendEventGeneratingActionAsync(
            NewOriginate(), outcome: null, TimeSpan.FromSeconds(30), CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]))
            .Should().BeTrue("the peer accepts the originate as Asterisk does");

        // The witness: an enumeration of the public overload, started after the originate's, that nothing completes.
        // DefaultEventTimeout ends it; by then the originate has waited longer than DefaultEventTimeout as well.
        var witness = ReadToEndAsync(connection.SendEventGeneratingActionAsync(new StatusAction()));
        var witnessId = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Success", witnessId, [new("EventList", "start")])).Should().BeTrue();
        var witnessEnded = await witness.WaitAsync(Bound);

        (await WriteOriginateResponseAsync(peer, id, "Success")).Should().BeTrue("the destination answered");
        var result = await originate.WaitAsync(Bound);

        var outcome = result.Events.OfType<OriginateResponseEvent>().ToList();
        using (new AssertionScope())
        {
            witnessEnded.Error.Should().BeOfType<OperationCanceledException>(
                "the public overload is bounded by DefaultEventTimeout, which ran out before the outcome was written");
            result.Error.Should().BeNull(
                "the originate's wait is bounded by the duration it was given plus DefaultResponseTimeout, not by "
                + "DefaultEventTimeout");
            outcome.Should().ContainSingle("Asterisk sent one OriginateResponse for the originate");
            outcome.FirstOrDefault()?.Response.Should().Be("Success");
            outcome.FirstOrDefault()?.Channel.Should().Be(OriginatedChannel);
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldEndWithOperationCanceled_WhenNoOutcomeArrivesWithinTheDurationPlusTheResponseTimeout()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();

        // No event timeout at all: only the bound the overload sets can end this wait.
        await using var connection = Create(factory,
            eventTimeout: TimeSpan.Zero, responseTimeout: TimeSpan.FromMilliseconds(300));
        var peer = await ConnectAsync(connection, factory, peerCts);

        var originate = ReadToEndAsync(connection.SendEventGeneratingActionAsync(
            NewOriginate(), outcome: null, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]))
            .Should().BeTrue("the peer accepts the originate and then never reports its outcome");

        var ended = await CompletesWithinBoundAsync(originate);
        using (new AssertionScope())
        {
            ended.Should().BeTrue(
                "the wait is bounded by the given duration plus DefaultResponseTimeout even with no DefaultEventTimeout");
            if (ended)
            {
                var result = await originate;
                result.Error.Should().BeOfType<OperationCanceledException>("the bound ran out before any outcome");
                result.Events.Should().BeEmpty("the peer sent no event for the originate");
            }
        }
    }

    private sealed record Ended(IReadOnlyList<ManagerEvent> Events, Exception? Error);

    private static OriginateAction NewOriginate() => new()
    {
        Channel = "Local/s@wait8",
        Context = "wait8",
        Exten = "s",
        Priority = 1,
        Timeout = 30_000,
        IsAsync = true,
    };

    /// <summary>The <c>OriginateResponse</c> Asterisk 22.9.0 wrote for an answered async originate, header for header.</summary>
    private static Task<bool> WriteOriginateResponseAsync(PipedSocket peer, string id, string outcome) =>
        peer.WriteEventAsync("OriginateResponse",
        [
            new("Privilege", "call,all"), new("ActionID", id), new("Response", outcome),
            new("Channel", OriginatedChannel), new("Context", "wait8"), new("Exten", "s"),
            new("Reason", "4"), new("Uniqueid", "1790763823.612"),
            new("CallerIDNum", "<unknown>"), new("CallerIDName", "<unknown>"),
        ]);

    private static AmiConnection Create(PipedSocketFactory factory, TimeSpan eventTimeout, TimeSpan responseTimeout) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultResponseTimeout = responseTimeout,
            DefaultEventTimeout = eventTimeout,
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
}

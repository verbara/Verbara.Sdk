using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The internal members of <c>Verbara.Sdk.Ami</c> that <c>Verbara.Sdk.Live</c> calls, each called here exactly as Live
/// calls it.
/// </summary>
/// <remarks>
/// <para>
/// The rule: until the next major version, 3.0, each of these members keeps its name and its signature. A Live package
/// of the 2.x line may run on any newer Ami package, because NuGet resolves Live's dependency on Ami as a minimum
/// version, and package validation compares only the public surface, so it cannot see an internal member go. A Live
/// built against one of these members, running on an Ami without it, fails at run time with
/// <see cref="MissingMethodException"/> when the live server starts.
/// </para>
/// <para>
/// This test is where the rule fails first: it names each member with the exact signature Live's call site binds to,
/// so removing one, renaming it, or changing a parameter or a return type breaks this project's build.
/// </para>
/// <list type="bullet">
/// <item><description><c>AmiConnection.FullyBooted</c>, a <see cref="Task"/>;</description></item>
/// <item><description>
/// <c>AmiConnection.SendEventGeneratingActionAsync(ManagerAction, EventActionOutcome, CancellationToken)</c>, an
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="ManagerEvent"/>;
/// </description></item>
/// <item><description>
/// <c>AmiConnection.SendEventGeneratingActionAsync(ManagerAction, EventActionOutcome?, TimeSpan, CancellationToken)</c>,
/// an <see cref="IAsyncEnumerable{T}"/> of <see cref="ManagerEvent"/>, which <c>OriginateAsync</c> calls with the
/// originate's own <c>Timeout</c> (since 2.7.0);
/// </description></item>
/// <item><description><c>new EventActionOutcome()</c>;</description></item>
/// <item><description><c>EventActionOutcome.Rejection</c>, a nullable <see cref="string"/>;</description></item>
/// <item><description><c>EventActionOutcome.SessionEnded</c>, a <see cref="bool"/>;</description></item>
/// <item><description>
/// <c>AmiConnection.Lost</c>, an event of <see cref="Action{T}"/> of a nullable <see cref="Exception"/>, which Live
/// subscribes to and unsubscribes from.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AmiInternalsUsedByLiveTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InternalMembersLiveCalls_ShouldKeepTheSignaturesLiveBindsTo_WhenAmiIsBuilt()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultEventTimeout = TimeSpan.Zero,
        }), factory, NullLogger<AmiConnection>.Instance);
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var peer = await loggedIn.WaitAsync(Bound);

        // AmiConnection.FullyBooted: its getter, returning exactly Task.
        var fullyBooted = connection.FullyBooted;
        Exactly<Task>(ref fullyBooted);

        // AmiConnection.Lost: subscribed and unsubscribed with a handler declared exactly as Live's is.
        Action<Exception?> onLost = _ => { };
        connection.Lost += onLost;
        connection.Lost -= onLost;

        // new EventActionOutcome(), and the overload by its exact parameter list and return type.
        var outcome = new EventActionOutcome();
        Func<ManagerAction, EventActionOutcome, CancellationToken, IAsyncEnumerable<ManagerEvent>> send =
            connection.SendEventGeneratingActionAsync;
        var events = connection.SendEventGeneratingActionAsync(new StatusAction(), outcome, CancellationToken.None);

        // The overload OriginateAsync calls, by its exact parameter list and return type; bound only, never enumerated.
        Func<ManagerAction, EventActionOutcome?, TimeSpan, CancellationToken, IAsyncEnumerable<ManagerEvent>> sendWithin =
            connection.SendEventGeneratingActionAsync;
        Exactly<IAsyncEnumerable<ManagerEvent>>(ref events);

        var read = ReadAllAsync(events);
        var action = await peer.ReadActionAsync(peerCts.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
        var id = PipedSocket.ActionIdOf(action);
        await peer.RespondAsync("Success", id, [new("EventList", "start")]);
        await peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "0")]);
        var received = await read.WaitAsync(Bound);

        // EventActionOutcome.Rejection and EventActionOutcome.SessionEnded: their getters, by their exact types.
        var rejection = outcome.Rejection;
        Exactly<string?>(ref rejection);
        var sessionEnded = outcome.SessionEnded;
        Exactly<bool>(ref sessionEnded);

        using (new AssertionScope())
        {
            send.Should().NotBeNull("the overload binds to the delegate Live's call site needs");
            sendWithin.Should().NotBeNull("the overload binds to the delegate OriginateAsync's call site needs");
            onLost.Should().NotBeNull("the event takes the handler type Live's call site declares");
            fullyBooted.IsCompleted.Should().BeFalse("the peer never reported FullyBooted on this session");
            received.Should().BeEmpty("the peer's list was empty");
            rejection.Should().BeNull("Asterisk refused nothing");
            sessionEnded.Should().BeFalse("Asterisk completed the list on a session that is still up");
        }
    }

    /// <summary>
    /// Compiles only when <paramref name="value"/>'s declared type is exactly <typeparamref name="T"/>: a <c>ref</c>
    /// argument takes no conversion, so a member whose type changed, even to a derived or generic one, stops compiling
    /// here.
    /// </summary>
    private static void Exactly<T>(ref T value) => GC.KeepAlive(value);

    private static async Task<List<ManagerEvent>> ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);

        return received;
    }
}

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// Replays a byte-exact AMI capture from <c>Recordings/asterisk-ami/</c> through the path a live
/// connection takes: <see cref="AmiProtocolReader"/> → <c>GeneratedEventDeserializer</c> →
/// <see cref="VerbaraServer"/>'s own event observer → <see cref="CallSessionManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// The server is started against a substitute <see cref="IAmiConnection"/> whose state-load actions
/// return nothing, and every event is delivered to the observer the server subscribed in
/// <see cref="VerbaraServer.StartAsync"/> — the production observer, not a copy of its switch. An
/// exception that observer throws is caught and recorded, and the replay moves on to the next event.
/// That is what <c>AmiConnection.DispatchEventAsync</c> does with it ("Observer errors should not
/// crash the pump"), so a replay reports both what a consumer observes and what the dispatcher
/// swallows.
/// </para>
/// <para>
/// Calls are attributed to scenarios by the capture's own marker frames (<c>UserEvent: N5Marker</c>
/// with <c>Scenario: &lt;id&gt;</c>; the folder's README explains them): a call belongs to the scenario
/// whose marker was the last one delivered before its <see cref="CallStartedEvent"/>.
/// </para>
/// <para>
/// Delivery is synchronous and in capture order, and every session transition runs on the delivering
/// thread, so the result is complete when <see cref="ReplayAsync"/> returns. Nothing waits.
/// </para>
/// </remarks>
internal static class AmiCaptureReplay
{
    /// <summary>
    /// The twelve-shape captures, one per Asterisk version (20.20.1, 22.9.0, 23.4.1). Each runs the
    /// same twelve scenarios, <c>S1</c> to <c>S12</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> CallShapeCaptures =
    [
        "call-shapes-asterisk-20.20.1.raw",
        "call-shapes-asterisk-22.9.0.raw",
        "call-shapes-asterisk-23.4.1.raw",
    ];

    private const string MarkerUserEvent = "N5Marker";
    private const string BeforeFirstMarker = "(before-the-first-marker)";

    /// <summary>
    /// Replays <paramref name="fixture"/> (a file name in <c>Recordings/asterisk-ami/</c>) into a
    /// fresh server and session manager, with the default in-memory session store.
    /// </summary>
    public static async Task<CaptureReplay> ReplayAsync(string fixture, SessionOptions? options = null)
    {
        var path = Path.Join(AppContext.BaseDirectory, "Recordings", "asterisk-ami", fixture);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Capture '{fixture}' is not in the test output. It is copied there from the suite's "
                + "Recordings/asterisk-ami/ folder by the project file.", path);

        var connection = Substitute.For<IAmiConnection>();
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NoEvents());
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
            .Returns(Substitute.For<IDisposable>());

        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await using var manager = new CallSessionManager(
            Options.Create(options ?? new SessionOptions()),
            NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        manager.AttachToServer(server, "replay");
        await server.StartAsync();
        if (observer is null)
            throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer.");

        var scenario = BeforeFirstMarker;
        var calls = new List<ReplayedCall>();
        var callsBySessionId = new Dictionary<string, ReplayedCall>(StringComparer.Ordinal);
        var swallowed = new List<SwallowedObserverException>();

        using var domainEvents = manager.Events.Subscribe(evt =>
        {
            if (evt is CallStartedEvent)
            {
                var session = manager.GetById(evt.SessionId)
                    ?? throw new InvalidOperationException(
                        $"CallStartedEvent for {evt.SessionId}, which the manager does not hold.");
                var call = new ReplayedCall(scenario, session);
                calls.Add(call);
                callsBySessionId[evt.SessionId] = call;
            }

            if (callsBySessionId.TryGetValue(evt.SessionId, out var target))
                target.Record(evt);
        });

        await using var stream = File.OpenRead(path);
        var reader = new AmiProtocolReader(PipeReader.Create(stream));
        while (await reader.ReadMessageAsync() is { } message)
        {
            // Responses go to the action that is waiting for them and the banner to the login, never
            // to an observer. Every event goes to the observers, including those that also complete
            // an event-generating action (AmiConnection.ReaderLoopAsync).
            if (!message.IsEvent)
                continue;

            var evt = GeneratedEventDeserializer.Deserialize(message);
            if (TryReadMarker(evt, out var next))
                scenario = next;

            try
            {
                observer.OnNext(evt);
            }
            catch (Exception ex)
            {
                // AmiConnection.DispatchEventAsync catches everything an observer throws and carries on.
                swallowed.Add(new SwallowedObserverException(scenario, evt.EventType ?? "", ex));
            }
        }

        return new CaptureReplay(fixture, calls, swallowed);
    }

    private static bool TryReadMarker(ManagerEvent evt, out string scenario)
    {
        scenario = "";
        if (!string.Equals(evt.EventType, "UserEvent", StringComparison.Ordinal) || evt.RawFields is not { } fields)
            return false;
        if (!fields.TryGetValue("UserEvent", out var name) || !string.Equals(name, MarkerUserEvent, StringComparison.Ordinal))
            return false;
        if (!fields.TryGetValue("Scenario", out var id))
            return false;

        scenario = id;
        return true;
    }

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>What one replay produced: every call it opened, and every exception the dispatcher swallowed.</summary>
internal sealed class CaptureReplay(
    string fixture,
    IReadOnlyList<ReplayedCall> calls,
    IReadOnlyList<SwallowedObserverException> observerExceptions)
{
    public string Fixture { get; } = fixture;

    /// <summary>Every call the replay opened, in the order it started.</summary>
    public IReadOnlyList<ReplayedCall> Calls { get; } = calls;

    /// <summary>Every exception the server's observer threw, in delivery order.</summary>
    public IReadOnlyList<SwallowedObserverException> ObserverExceptions { get; } = observerExceptions;

    /// <summary>
    /// The one call scenario <paramref name="scenarioId"/> (<c>"S1"</c> … <c>"S12"</c>) produced.
    /// Throws when the scenario produced none or several, since every pin assumes one call each.
    /// </summary>
    public ReplayedCall Call(string scenarioId)
    {
        var matches = Calls.Where(c => string.Equals(c.ScenarioId, scenarioId, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"{Fixture}: scenario {scenarioId} produced {matches.Count} calls, expected 1.{Environment.NewLine}{Describe()}");
    }

    /// <summary>One line per call, for assertion messages.</summary>
    public string Describe() =>
        $"{Fixture}:{Environment.NewLine}" + string.Join(Environment.NewLine, Calls.Select(c => c.ToString()));
}

/// <summary>A call a replay opened: the scenario it belongs to, its session, and its domain events.</summary>
internal sealed class ReplayedCall(string scenario, CallSession session)
{
    private readonly List<SessionDomainEvent> _domainEvents = [];

    /// <summary>The marker's full scenario name, e.g. <c>S5-originate-never-answered</c>.</summary>
    public string Scenario { get; } = scenario;

    /// <summary>The scenario's short id, e.g. <c>S5</c>.</summary>
    public string ScenarioId => Scenario.Split('-', 2)[0];

    public CallSession Session { get; } = session;

    /// <summary>The domain events the manager published for this call, in order.</summary>
    public IReadOnlyList<SessionDomainEvent> DomainEvents => _domainEvents;

    /// <summary>The call's audit trail, as event types.</summary>
    public IReadOnlyList<CallSessionEventType> Trail => [.. Session.Events.Select(e => e.Type)];

    internal void Record(SessionDomainEvent evt) => _domainEvents.Add(evt);

    public override string ToString() =>
        $"{Scenario}: state={Session.State} cause={Session.HangupCause?.ToString() ?? "null"} "
        + $"dialingAt={SetOrNull(Session.DialingAt)} queuedAt={SetOrNull(Session.QueuedAt)} "
        + $"connectedAt={SetOrNull(Session.ConnectedAt)} talk={SetOrNull(Session.TalkTime)} "
        + $"trail=[{string.Join(">", Trail)}] "
        + $"events=[{string.Join(",", DomainEvents.Select(Describe))}]";

    private static string SetOrNull<T>(T? value) where T : struct => value.HasValue ? "set" : "null";

    private static string Describe(SessionDomainEvent evt) => evt switch
    {
        CallEndedEvent e => $"CallEnded(cause={e.Cause?.ToString() ?? "null"},talk={SetOrNull(e.TalkTime)})",
        _ => evt.GetType().Name,
    };
}

/// <summary>An exception the server's observer threw into the dispatcher, and the scenario it came from.</summary>
internal sealed record SwallowedObserverException(string Scenario, string EventType, Exception Exception)
{
    public override string ToString() =>
        $"[{Scenario}] {EventType}: {Exception.GetType().Name}: {Exception.Message}";
}

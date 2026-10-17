namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.EventOrdering;

using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class SessionEventOrderTests : FunctionalTestBase
{
    // ────────────────────────────────────────────────────────────────────────
    //  Factory helpers
    // ────────────────────────────────────────────────────────────────────────

    private CallSessionManager CreateSessionManager() =>
        new(
            Options.Create(new SessionOptions()),
            LoggerFactory.CreateLogger<CallSessionManager>(),
            new NullSessionStore());

    // ────────────────────────────────────────────────────────────────────────
    //  Tests
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A call that hangs up in under 1 second must still produce a completed session
    /// (CallStartedEvent + CallEndedEvent).
    /// </summary>
    [Fact]
    public async Task QuickHangup_ShouldStillCreateSession()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        await using var sessionManager = CreateSessionManager();
        sessionManager.AttachToServer(server, "test");

        var domainEvents = new ConcurrentBag<SessionDomainEvent>();
        using var sub = sessionManager.Events.Subscribe(e => domainEvents.Add(e));

        // Track created channels for immediate hangup
        var createdChannelNames = new ConcurrentBag<string>();
        server.Channels.ChannelAdded += ch => createdChannelNames.Add(ch.Name);

        // Originate then immediately hang up within ~200 ms
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "10",
            IsAsync = true,
            ActionId = "quick-hangup-01"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        foreach (var channelName in createdChannelNames)
        {
            try
            {
                await connection.SendActionAsync(new HangupAction
                {
                    Channel = channelName,
                    Cause = 16
                });
            }
            catch (OperationCanceledException)
            {
                // Channel may have already gone; acceptable
            }
        }

        // Wait for all AMI events to propagate to the session manager
        await Task.Delay(TimeSpan.FromSeconds(4));

        // At least one session must have been created and completed
        var started = domainEvents.OfType<CallStartedEvent>().ToList();
        var ended = domainEvents.OfType<CallEndedEvent>().ToList();

        started.Should().NotBeEmpty(
            "a quick-hangup call must still generate a CallStartedEvent");
        ended.Should().NotBeEmpty(
            "a quick-hangup call must generate a CallEndedEvent even if it lasted <1 s");

        // Every ended session must have had a start event first
        foreach (var endEvt in ended)
        {
            started.Should().Contain(s => s.SessionId == endEvt.SessionId,
                "CallStartedEvent must precede CallEndedEvent for session {0}", endEvt.SessionId);
        }
    }

    /// <summary>
    /// 5 concurrent calls: each session must be correlated to its own channels
    /// via LinkedId — no cross-contamination between sessions.
    /// </summary>
    [Fact]
    public async Task ConcurrentSessions_ShouldCorrelateByLinkedId()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        await using var sessionManager = CreateSessionManager();
        sessionManager.AttachToServer(server, "test");

        // Launch 5 concurrent calls
        const int callCount = 5;
        var tasks = Enumerable.Range(0, callCount).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/100@test-functional",
                    Application = "Wait",
                    Data = "3",
                    IsAsync = true,
                    ActionId = $"concurrent-sess-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        });

        await Task.WhenAll(tasks);

        // Allow sessions to form
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Each active session must have at least one participant
        var activeSessions = sessionManager.ActiveSessions.ToList();
        foreach (var session in activeSessions)
        {
            session.Participants.Should().NotBeEmpty(
                "session {0} must have at least one participant channel", session.SessionId);
        }

        // LinkedId-based correlation: every participant's channel must map back to the session
        foreach (var session in activeSessions)
        {
            foreach (var participant in session.Participants)
            {
                var lookup = sessionManager.GetByChannelId(participant.UniqueId);
                lookup.Should().NotBeNull(
                    "participant channel {0} must map back to a session via GetByChannelId",
                    participant.UniqueId);

                lookup!.SessionId.Should().Be(session.SessionId,
                    "channel {0} must belong to session {1}, not {2}",
                    participant.UniqueId, session.SessionId, lookup.SessionId);
            }

            // LinkedId lookup must also work
            var byLinked = sessionManager.GetByLinkedId(session.LinkedId);
            byLinked.Should().NotBeNull(
                "session {0} must be retrievable by its LinkedId", session.SessionId);
        }
    }

    /// <summary>
    /// For a single call lifecycle, session domain events must fire in causal order:
    /// CallStartedEvent before CallEndedEvent.
    /// </summary>
    [Fact]
    public async Task SessionEvents_ShouldFireInCausalOrder()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());
        await server.StartAsync();

        await using var sessionManager = CreateSessionManager();
        sessionManager.AttachToServer(server, "test");

        // Collect domain events in arrival order
        var eventLog = new ConcurrentQueue<(int Index, SessionDomainEvent Event)>();
        var counter = 0;
        using var sub = sessionManager.Events.Subscribe(e =>
        {
            var idx = Interlocked.Increment(ref counter);
            eventLog.Enqueue((idx, e));
        });

        // Single call with a short Wait so it completes naturally. The originate names the call, so the test reads
        // only its own session: a call an earlier test left may end inside this window.
        var callId = $"causal-order-{Guid.NewGuid():N}";
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "2",
            IsAsync = true,
            ChannelId = callId,
            ActionId = "causal-order-01"
        });

        // The session the call belongs to, read while the call is up (it lasts the 2 s Wait).
        string? sessionId = null;
        var sessionDeadline = DateTime.UtcNow.AddSeconds(3);
        while (sessionId is null && DateTime.UtcNow < sessionDeadline)
        {
            sessionId = sessionManager.GetByChannelId(callId)?.SessionId;
            if (sessionId is null)
                await Task.Delay(TimeSpan.FromMilliseconds(50)); // fence-allow: GUARD-TIMEOUT — polls for the observed session; a green run leaves as soon as it exists
        }

        (sessionId is not null).Should().BeTrue("the originated call {0} must belong to a session within 3 s", callId);

        // Wait for the full lifecycle: the 2 s Wait, the hangup and its propagation.
        var endDeadline = DateTime.UtcNow.AddSeconds(8);
        while (!eventLog.Any(x => x.Event.SessionId == sessionId && x.Event is CallEndedEvent) && DateTime.UtcNow < endDeadline)
            await Task.Delay(TimeSpan.FromMilliseconds(50)); // fence-allow: GUARD-TIMEOUT — polls for the observed CallEnded; a green run leaves as soon as it arrives

        var ordered = eventLog.Where(x => x.Event.SessionId == sessionId).OrderBy(x => x.Index).ToList();
        var eventTypes = string.Join(" → ", ordered.Select(x => x.Event.GetType().Name));

        var startIndex = ordered.FindIndex(x => x.Event is CallStartedEvent);
        var endIndex = ordered.FindIndex(x => x.Event is CallEndedEvent);
        startIndex.Should().BeGreaterThanOrEqualTo(0,
            "the call's session {0} must report a CallStartedEvent. Actual order: {1}", sessionId, eventTypes);
        endIndex.Should().BeGreaterThanOrEqualTo(0,
            "the call ends after its 2 s Wait, so session {0} must report a CallEndedEvent within 8 s. Actual order: {1}",
            sessionId, eventTypes);
        startIndex.Should().BeLessThan(endIndex,
            "CallStartedEvent (position {0}) must precede CallEndedEvent (position {1}) for session {2}. Actual order: {3}",
            startIndex, endIndex, sessionId, eventTypes);

        // Timestamps must be non-decreasing
        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i].Event.Timestamp.Should().BeOnOrAfter(ordered[i - 1].Event.Timestamp,
                "event timestamps must be non-decreasing within a session. Actual order: {0}", eventTypes);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>No-op session store for functional tests.</summary>
    private sealed class NullSessionStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
            => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<CallSession?>(null);
    }
}

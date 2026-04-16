using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Diagnostics;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.IntegrationTests.Sessions;

[Collection("Integration")]
[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal is handled via IAsyncLifetime.DisposeAsync")]
public sealed class SessionIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private Asterisk.Sdk.Ami.Connection.AmiConnection? _connection;
    private AsteriskServer? _server;
    private CallSessionManager? _sessionManager;

    public SessionIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connection = AsteriskFixture.CreateAmiConnection(_fixture);
        await _connection.ConnectAsync();

        _server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
        await _server.StartAsync();

        var options = Options.Create(new SessionOptions());
        _sessionManager = new CallSessionManager(
            options,
            NullLogger<CallSessionManager>.Instance,
            new TestSessionStore());
        _sessionManager.AttachToServer(_server, "integration-test");
    }

    public async Task DisposeAsync()
    {
        if (_sessionManager is not null)
            await _sessionManager.DisposeAsync();
        if (_server is not null)
            await _server.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Session_ShouldBeCreated_WhenCallOriginated()
    {
        // Arrange — subscribe to domain events to detect session creation and completion
        var created = new TaskCompletionSource<CallStartedEvent>();
        var ended = new TaskCompletionSource<CallEndedEvent>();

        using var sub = _sessionManager!.Events.Subscribe(new DomainEventObserver(created, ended));

        // Act — originate a call that will fail quickly (no real extension)
        await _connection!.SendActionAsync(new OriginateAction
        {
            Channel = "Local/s@default",
            Context = "default",
            Exten = "s",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Assert — session should be created within a reasonable time
        var startedEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(15));
        startedEvent.Should().NotBeNull();
        startedEvent.SessionId.Should().NotBeNullOrEmpty();

        var session = _sessionManager.GetById(startedEvent.SessionId);
        session.Should().NotBeNull();
        session!.Participants.Should().NotBeEmpty();
        session.State.Should().NotBe(CallSessionState.Completed,
            "session should be in an active state right after creation");

        // Wait for the call to end (it should fail/complete quickly since there is no real destination)
        var endedEvent = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        endedEvent.Should().NotBeNull();
        endedEvent.SessionId.Should().Be(startedEvent.SessionId);

        var completedSession = _sessionManager.GetById(endedEvent.SessionId);
        completedSession.Should().NotBeNull();
        completedSession!.State.Should().BeOneOf(
            [CallSessionState.Completed, CallSessionState.Failed],
            "session should reach a terminal state after hangup");
    }

    [Fact]
    public async Task Sessions_ShouldBeIndependent_WhenTwoCallsOriginated()
    {
        // Arrange
        var createdSessions = new List<CallStartedEvent>();
        var gate = new TaskCompletionSource<bool>();
        int count = 0;

        using var sub = _sessionManager!.Events.Subscribe(new ActionObserver<SessionDomainEvent>(evt =>
        {
            if (evt is CallStartedEvent started)
            {
                lock (createdSessions)
                {
                    createdSessions.Add(started);
                    if (Interlocked.Increment(ref count) >= 2)
                        gate.TrySetResult(true);
                }
            }
        }));

        // Act — originate two independent calls
        await _connection!.SendActionAsync(new OriginateAction
        {
            Channel = "Local/s@default",
            Context = "default",
            Exten = "s",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        await _connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/t@default",
            Context = "default",
            Exten = "t",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Assert — two distinct sessions should be created
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(15));

        createdSessions.Should().HaveCountGreaterOrEqualTo(2);
        createdSessions.Select(s => s.SessionId).Distinct().Should().HaveCountGreaterOrEqualTo(2,
            "each call should get its own session");
    }

    [Fact]
    public async Task SessionMetrics_ShouldReflectSessionCounts()
    {
        // Arrange — capture baseline metric values using a MeterListener
        long createdBefore = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Asterisk.Sdk.Sessions")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "sessions.created")
                Interlocked.Add(ref createdBefore, measurement);
        });
        listener.Start();

        var sessionCreated = new TaskCompletionSource<CallStartedEvent>();
        using var sub = _sessionManager!.Events.Subscribe(
            new ActionObserver<SessionDomainEvent>(evt =>
            {
                if (evt is CallStartedEvent started)
                    sessionCreated.TrySetResult(started);
            }));

        // Act — originate a call
        await _connection!.SendActionAsync(new OriginateAction
        {
            Channel = "Local/s@default",
            Context = "default",
            Exten = "s",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Force a flush
        listener.RecordObservableInstruments();

        // Assert — sessions.created counter should have incremented
        Interlocked.Read(ref createdBefore).Should().BeGreaterOrEqualTo(1,
            "at least one session.created metric event should have fired");
    }

    // --- Helper observers ---

    private sealed class DomainEventObserver(
        TaskCompletionSource<CallStartedEvent> created,
        TaskCompletionSource<CallEndedEvent> ended) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallStartedEvent started) created.TrySetResult(started);
            if (value is CallEndedEvent callEnded) ended.TrySetResult(callEnded);
        }

        public void OnError(Exception error)
        {
            created.TrySetException(error);
            ended.TrySetException(error);
        }

        public void OnCompleted() { }
    }

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// Simple in-memory store for integration tests (avoids using internal InMemorySessionStore).
    /// </summary>
    private sealed class TestSessionStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, CallSession> _store = new();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _store[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult(_store.GetValueOrDefault(sessionId));

        public override ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
            => ValueTask.FromResult(_store.Values.Where(s =>
                s.State is not CallSessionState.Completed
                and not CallSessionState.Failed
                and not CallSessionState.TimedOut));

        public override ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        {
            _store.TryRemove(sessionId, out _);
            return ValueTask.CompletedTask;
        }
    }
}

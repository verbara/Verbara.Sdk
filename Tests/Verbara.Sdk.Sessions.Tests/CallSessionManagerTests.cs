using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ReceivedExtensions;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class CallSessionManagerTests : IAsyncDisposable
{
    private readonly CallSessionManager _sut;
    private readonly VerbaraServer _server;
    private readonly IAmiConnection _connection;

    public CallSessionManagerTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        var options = Options.Create(new SessionOptions());
        _sut = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void AttachToServer_ShouldAcceptServer()
    {
        Action act = () => _sut.AttachToServer(_server, "srv-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void NewChannel_ShouldCreateSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1", context: "from-trunk");

        var session = _sut.GetByLinkedId("linked-1");
        session.Should().NotBeNull();
        session!.Direction.Should().Be(CallDirection.Inbound);
        session.Participants.Should().HaveCount(1);
        session.Participants[0].Role.Should().Be(ParticipantRole.Caller);
    }

    [Fact]
    public void SecondChannel_ShouldAddAsDestination()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "PJSIP/200-001", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants.Should().HaveCount(2);
        session.Participants[1].Role.Should().Be(ParticipantRole.Destination);
    }

    [Fact]
    public void LocalChannel_ShouldBeMarkedInternal()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "Local/100@default-001;1", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants[1].Role.Should().Be(ParticipantRole.Internal);
    }

    [Fact]
    public void GetByChannelId_ShouldFindSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByChannelId("uid-1").Should().NotBeNull();
    }

    [Fact]
    public void ChannelHangup_ShouldCompleteSession_WhenAllParticipantsLeft()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up,
            linkedId: "linked-1");
        _server.Channels.OnHangup("uid-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);
    }

    [Fact]
    public void ActiveSessions_ShouldReturnNonCompleted()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.ActiveSessions.Should().HaveCount(1);
    }

    [Fact]
    public void DetachFromServer_ShouldUnsubscribe()
    {
        _sut.AttachToServer(_server, "srv-1");
        _sut.DetachFromServer("srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByLinkedId("linked-1").Should().BeNull();
    }

    [Fact]
    public async Task OnChannelAdded_ShouldDelegateToSessionStore()
    {
        var store = Substitute.For<SessionStoreBase>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        store.SaveAsync(Arg.Any<CallSession>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012

        var options = Options.Create(new SessionOptions());
        await using var manager = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store);
        manager.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        // Allow fire-and-forget task to complete
        await Task.Delay(50);

        var saveCalls = store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(SessionStoreBase.SaveAsync))
            .ToList();
        saveCalls.Should().HaveCountGreaterOrEqualTo(1);
        var savedSession = (CallSession)saveCalls[0].GetArguments()[0]!;
        savedSession.LinkedId.Should().Be("linked-1");
    }

    [Fact]
    public async Task OnSessionCompleted_ShouldPersistToStore()
    {
        var store = Substitute.For<SessionStoreBase>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        store.SaveAsync(Arg.Any<CallSession>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012

        var options = Options.Create(new SessionOptions());
        await using var manager = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store);
        manager.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up,
            linkedId: "linked-1");
        _server.Channels.OnHangup("uid-1");

        // Allow fire-and-forget tasks to complete
        await Task.Delay(50);

        // At least 2 saves: one on creation, one on completion
        var saveCalls = store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(SessionStoreBase.SaveAsync))
            .ToList();
        saveCalls.Should().HaveCountGreaterOrEqualTo(2, "expected at least creation + completion saves");
        var lastSavedSession = (CallSession)saveCalls[^1].GetArguments()[0]!;
        lastSavedSession.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);
    }

    /// <summary>How a store reports a save that was cut short.</summary>
    public enum SaveCancellation
    {
        /// <summary>Awaiting the save throws <see cref="TaskCanceledException"/>, as a cancelled task does.</summary>
        TaskCanceled,

        /// <summary>
        /// Awaiting the save throws a plain <see cref="OperationCanceledException"/>, as
        /// <c>ct.ThrowIfCancellationRequested()</c> in the Redis and Postgres stores does.
        /// </summary>
        OperationCanceled,
    }

    [Theory]
    [InlineData(SaveCancellation.TaskCanceled)]
    [InlineData(SaveCancellation.OperationCanceled)]
    public async Task OnChannelAdded_ShouldNotLogPersistError_WhenShutdownCancelsInFlightSave(
        SaveCancellation cancellation)
    {
        var store = new PendingSaveStore(cancellation);
        var logger = new RecordingLogger();
        using var shutdown = new CancellationTokenSource();
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()), logger, store);
        manager.SetShutdownToken(shutdown.Token);
        manager.AttachToServer(_server, "srv-1");
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;
        save.Token.Should().Be(shutdown.Token, "the manager saves under its shutdown token");
        save.IsObserved.Should().BeFalse("the save is still in flight");

        shutdown.Cancel();

        save.IsObserved.Should().BeTrue(
            "cancelling the token ended the save and resumed the persist before Cancel returned");
        var expectedFailure = cancellation == SaveCancellation.TaskCanceled
            ? typeof(TaskCanceledException)
            : typeof(OperationCanceledException);
        save.Failure.Should().BeOfType(expectedFailure);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task OnChannelAdded_ShouldNotLogPersistError_WhenSaveStartsAfterShutdownCancelled()
    {
        var store = new TokenCheckingStore();
        var logger = new RecordingLogger();
        using var shutdown = new CancellationTokenSource();
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()), logger, store);
        manager.SetShutdownToken(shutdown.Token);
        manager.AttachToServer(_server, "srv-1");
        shutdown.Cancel();

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        manager.GetByLinkedId("linked-1").Should().NotBeNull();
        var save = store.Saves.Should().ContainSingle().Subject;
        save.IsCanceled.Should().BeTrue(
            "the save failed its token check before its first await, so the persist awaited a finished " +
            "save and ran its catch before OnNewChannel returned");
        await FluentActions.Awaiting(() => save).Should().ThrowExactlyAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(SaveCancellation.TaskCanceled)]
    [InlineData(SaveCancellation.OperationCanceled)]
    public async Task OnChannelAdded_ShouldLogPersistError_WhenStoreCancelsSaveWithoutShutdown(
        SaveCancellation cancellation)
    {
        var store = new PendingSaveStore(cancellation);
        var logger = new RecordingLogger();
        using var shutdown = new CancellationTokenSource();
        await using var manager = new CallSessionManager(Options.Create(new SessionOptions()), logger, store);
        manager.SetShutdownToken(shutdown.Token);
        manager.AttachToServer(_server, "srv-1");
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        // The store gives up on the save by itself, the way a store-side timeout would.
        store.AbandonPendingSaves();

        shutdown.IsCancellationRequested.Should().BeFalse();
        save.IsObserved.Should().BeTrue(
            "abandoning the save resumed the persist before AbandonPendingSaves returned");
        var session = manager.GetByLinkedId("linked-1");
        session.Should().NotBeNull();
        var error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.EventId.Id.Should().Be(974837145);
        error.EventId.Name.Should().Be("LogPersistError");
        error.Message.Should().Be($"Failed to persist session {session!.SessionId}");
        error.Exception.Should().BeSameAs(save.Failure);
    }

    /// <summary>
    /// A store whose saves stay in flight until the token they were handed is cancelled, or until
    /// the store abandons them itself. Either way the save ends with the exception
    /// <see cref="SaveCancellation"/> names.
    /// </summary>
    private sealed class PendingSaveStore(SaveCancellation cancellation) : SessionStoreBase
    {
        private readonly ConcurrentQueue<PendingSave> _saves = new();

        public IReadOnlyCollection<PendingSave> Saves => _saves.ToArray();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            var save = new PendingSave(ct);
            _saves.Enqueue(save);
            ct.Register(() => save.End(Cancelled(ct)));
            return new ValueTask(save, 0);
        }

        public void AbandonPendingSaves()
        {
            foreach (var save in _saves)
                save.End(Cancelled(CancellationToken.None));
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);

        private OperationCanceledException Cancelled(CancellationToken token) =>
            cancellation == SaveCancellation.TaskCanceled
                ? new TaskCanceledException(null, null, token)
                : new OperationCanceledException(token);
    }

    /// <summary>
    /// One in-flight save, and the source of the <see cref="ValueTask"/> the manager awaits. The
    /// manager's await leaves its continuation here, and <see cref="End"/> runs that continuation on
    /// the calling thread instead of scheduling it, ignoring the await's scheduling flags on
    /// purpose. So when <see cref="End"/> returns, the persist has resumed, read the exception and
    /// finished its catch, whether or not its await captured a synchronization context.
    /// </summary>
    private sealed class PendingSave : IValueTaskSource
    {
        private readonly Lock _gate = new();
        private Action<object?>? _continuation;
        private object? _continuationState;
        private Exception? _failure;
        private bool _observed;

        public PendingSave(CancellationToken token) => Token = token;

        public CancellationToken Token { get; }

        /// <summary>The exception the save ended with, or <see langword="null"/> while it is in flight.</summary>
        public Exception? Failure
        {
            get
            {
                lock (_gate)
                    return _failure;
            }
        }

        /// <summary>True once the awaiting persist has read how the save ended.</summary>
        public bool IsObserved
        {
            get
            {
                lock (_gate)
                    return _observed;
            }
        }

        public void End(Exception failure)
        {
            Action<object?>? continuation;
            object? state;
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                _failure = failure;
                continuation = _continuation;
                state = _continuationState;
                _continuation = null;
                _continuationState = null;
            }

            continuation?.Invoke(state);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            lock (_gate)
            {
                return _failure switch
                {
                    null => ValueTaskSourceStatus.Pending,
                    OperationCanceledException => ValueTaskSourceStatus.Canceled,
                    _ => ValueTaskSourceStatus.Faulted,
                };
            }
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            lock (_gate)
            {
                if (_failure is null)
                {
                    _continuation = continuation;
                    _continuationState = state;
                    return;
                }
            }

            continuation(state);
        }

        public void GetResult(short token)
        {
            Exception failure;
            lock (_gate)
            {
                failure = _failure ?? throw new InvalidOperationException("The save is still in flight.");
                _observed = true;
            }

            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// A store whose saves open the way <c>RedisSessionStore.SaveAsync</c> and
    /// <c>PostgresSessionStore.SaveAsync</c> do: an async method whose first statement is
    /// <c>ct.ThrowIfCancellationRequested()</c>. A save started under a cancelled token therefore
    /// ends before its first await, as a cancelled task that throws a plain
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    private sealed class TokenCheckingStore : SessionStoreBase
    {
        private readonly ConcurrentQueue<Task> _saves = new();

        public IReadOnlyCollection<Task> Saves => _saves.ToArray();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            var save = SaveCoreAsync(ct);
            _saves.Enqueue(save);
            return new ValueTask(save);
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);

        private static async Task SaveCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield(); // Stands in for the round trip to the backing store.
        }
    }

    /// <summary>Keeps every entry written to it, so a test can assert on level, event and message.</summary>
    private sealed class RecordingLogger : ILogger<CallSessionManager>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

public sealed class SessionManagerHostedServiceTests : IAsyncDisposable
{
    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _server;

    public SessionManagerHostedServiceTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldAttachToServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);

        // Verify that AttachToServer was called by checking that the manager
        // responds to server events (no exception = attached successfully).
        // We verify indirectly: a second attach with the same serverId would replace
        // subscriptions without error, showing the first attach happened.
        var act = () => csm.AttachToServer(_server, "default");
        act.Should().NotThrow();

        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldDetachFromServer_WhenConcreteCallSessionManager()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        // After detach, re-attaching should work cleanly
        var act = () => csm.AttachToServer(_server, "default");
        act.Should().NotThrow();

        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager()
    {
        var mockManager = Substitute.For<ICallSessionManager>();
        var sut = new SessionManagerHostedService(mockManager, _server);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager()
    {
        var mockManager = Substitute.For<ICallSessionManager>();
        var sut = new SessionManagerHostedService(mockManager, _server);

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldReturnCompletedTask()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        var task = sut.StartAsync(CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
        await csm.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldReturnCompletedTask()
    {
        var csm = CreateCallSessionManager();
        var sut = new SessionManagerHostedService(csm, _server);

        var task = sut.StopAsync(CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
        await csm.DisposeAsync();
    }

    // --- The token session persistence runs under (spec: session-persistence-lifecycle) ---
    //
    // The service hands CallSessionManager the token EVERY save runs under, from the first channel
    // event to the last. These tests pin which phase may cancel it: the stop phase and teardown,
    // never the start phase.

    [Fact]
    public async Task StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var start = new CancellationTokenSource();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(start.Token);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;
        save.IsObserved.Should().BeFalse("the save is in flight before the start is aborted");

        start.Cancel();

        save.Token.IsCancellationRequested.Should().BeFalse(
            "the token the host passes to StartAsync means the start was aborted, and aborting a " +
            "start must not cancel the token every save runs under");
        save.Token.Should().NotBe(
            start.Token,
            "the service saves under a token from a source it owns itself, not the host's start token");
        save.IsObserved.Should().BeFalse("the save is still in flight, because nothing cancelled its token");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;
        await sut.StopAsync(stop.Token);
        save.IsObserved.Should().BeFalse("entering StopAsync does not cut the save short by itself");

        stop.Cancel();

        save.IsObserved.Should().BeTrue(
            "the host withdrew its grace, which cancelled the service's source and so ended the save " +
            "and resumed the persist before Cancel returned");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(
            e => e.Level >= LogLevel.Error,
            "a save the shutdown cut short is not a persistence failure");
    }

    [Fact]
    public async Task StopAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStopTokenIsNeverCancelled()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        await sut.StopAsync(stop.Token);

        save.Token.IsCancellationRequested.Should().BeFalse(
            "the stop is inside its budget, so nothing may cancel the persistence token");
        save.IsObserved.Should().BeFalse(
            "a graceful stop is exactly the case in which the session state is still worth " +
            "persisting, so the save keeps its budget");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StopAsync_ShouldNotStartASave_WhenAServerEventArrivesAfterTheStop()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(stop.Token);

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");

        store.Saves.Should().BeEmpty(
            "the service unsubscribes from the server before it wires the host's stop token, so a " +
            "channel event after the stop starts no save at all");
        manager.GetByLinkedId("linked-1").Should().BeNull("the manager is no longer subscribed to the server");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StopAsync_ShouldNotStartASave_WhenTheStopTokenIsAlreadyCancelledAndAnEventArrivesInline()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        // The instant of cancellation is the only moment at which the order of the two statements in
        // StopAsync is observable, so the event is raised from the persistence token's own
        // cancellation callback rather than after StopAsync has returned. An already-cancelled stop
        // token makes the service's source cancel inline from inside StopAsync — the real "no longer
        // graceful" shape — so this callback runs between the two statements, whichever way round
        // they are written. Causal throughout: no clock decides when the event arrives.
        save.Token.Register(() => _server.Channels.OnNewChannel(
            "uid-2", "PJSIP/100-002", ChannelState.Ring, linkedId: "linked-2"));
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await sut.StopAsync(withdrawn.Token);

        save.Token.IsCancellationRequested.Should().BeTrue(
            "the withdrawn grace cancelled the persistence token, so the callback above really ran " +
            "— without this the rest of the test could pass by the event never being raised at all");
        store.Saves.Should().ContainSingle(
            "the service unsubscribes from the server before it wires the host's stop token, so the " +
            "cancellation cannot reach a still-subscribed manager and no second save is started");
        manager.GetByLinkedId("linked-2").Should().BeNull(
            "the manager was already detached when its persistence token was cancelled");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Dispose_ShouldNotThrow_WhenCalledMoreThanOnce()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateCallSessionManager(store, new RecordingLogger());
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);

        var disposable = sut.Should().BeAssignableTo<IDisposable>().Subject;
        disposable.Dispose();

        // IDisposable's contract: a second Dispose must be ignored, not throw. The service owns a
        // CancellationTokenSource and Cancel on a disposed source throws ObjectDisposedException, so
        // releasing without a guard turns the second call into a fault. The container disposes once,
        // but a type whose Dispose is not idempotent is a landmine for anyone who wraps it.
        var second = () => disposable.Dispose();
        second.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        await sut.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        // BeAssignableTo rather than `using var sut` or a direct `sut.Dispose()`: the service is
        // sealed, so both would be a compile error while it is not disposable — and this test has to
        // compile, and fail, against a service that does not yet own the source it hands out.
        var disposable = sut.Should().BeAssignableTo<IDisposable>(
            "the service owns the cancellation source it hands the manager, so it has to release it").Which;
        disposable.Dispose();

        save.IsObserved.Should().BeTrue(
            "teardown cancelled the source before releasing it, so the save ended rather than being " +
            "left under a token nothing can ever cancel");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        var parked = new ParkedStartHostedService();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(sut);
                services.AddSingleton<IHostedService>(parked);
            })
            .Build();
        using var start = new CancellationTokenSource();

        var starting = host.StartAsync(start.Token);

        // The second hosted service parks the start, so this one is started and the host's start
        // token is still live. Waiting on the park's own signal, never on a clock.
        await Task.WhenAny(parked.Entered, starting);
        parked.Entered.IsCompletedSuccessfully.Should().BeTrue(
            "the host started this service and then parked inside the next one");
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        start.Cancel();

        save.Token.IsCancellationRequested.Should().BeFalse(
            "cancelling the token passed to Host.StartAsync aborts the start, and the manager must " +
            "not be saving under it");
        save.IsObserved.Should().BeFalse("the save is still in flight");
        await FluentActions.Awaiting(() => starting).Should().ThrowAsync<OperationCanceledException>(
            "the parked hosted service ends the start as aborted");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateCallSessionManager(store, logger);
        var sut = new SessionManagerHostedService(manager, _server);
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<IHostedService>(sut))
            .Build();
        await host.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        // An already-cancelled token handed to Host.StopAsync, not HostOptions.ShutdownTimeout: the
        // host turns that option into CancelAfter on a linked source, and CancelAfter fires on a
        // timer even at zero, so a timeout would be a race. Host.StopAsync links the caller's token
        // into the token it passes each service, so an already-cancelled one arrives cancelled.
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await host.StopAsync(withdrawn.Token);

        save.IsObserved.Should().BeTrue(
            "the shutdown was no longer graceful when it reached the service, so the save was cut " +
            "short instead of outliving the host");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(
            e => e.Level >= LogLevel.Error,
            "a save the shutdown cut short is not a persistence failure");
    }

    private static CallSessionManager CreateCallSessionManager()
    {
        var options = Options.Create(new SessionOptions());
        var store = new TestSessionStore();
        return new CallSessionManager(options, NullLogger<CallSessionManager>.Instance, store);
    }

    private static CallSessionManager CreateCallSessionManager(
        SessionStoreBase store,
        ILogger<CallSessionManager> logger) =>
        new(Options.Create(new SessionOptions()), logger, store);

    /// <summary>
    /// Minimal session store for testing (InMemorySessionStore is internal to the Sessions assembly).
    /// </summary>
    private sealed class TestSessionStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    /// <summary>
    /// A store that records the token each save was handed and keeps that save in flight until the
    /// token is cancelled, when the save ends with an <see cref="OperationCanceledException"/>. The
    /// delay-free shape of <c>CallSessionManagerTests.PendingSaveStore</c>: the registration ends the
    /// save on the cancelling thread, so the persist has resumed and finished its catch before
    /// <see cref="CancellationTokenSource.Cancel()"/> returns and the assertions read the result. No
    /// wall-clock wait ever stands in for "it has finished reacting".
    /// </summary>
    private sealed class PendingSaveStore : SessionStoreBase
    {
        private readonly ConcurrentQueue<PendingSave> _saves = new();

        public IReadOnlyCollection<PendingSave> Saves => _saves.ToArray();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            var save = new PendingSave(ct);
            _saves.Enqueue(save);
            ct.Register(() => save.End(new OperationCanceledException(ct)));
            return new ValueTask(save, 0);
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
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

        /// <summary>The token this save was handed, which is the token the manager persists under.</summary>
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
    /// A hosted service that parks the host's start until the token the host handed it is cancelled,
    /// and then ends the start as aborted. Registered after the service under test, so the host has
    /// started that one and its own start token is still live while the park holds.
    /// </summary>
    private sealed class ParkedStartHostedService : IHostedService
    {
        private readonly TaskCompletionSource _parked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the host has reached this service's start.</summary>
        public Task Entered => _entered.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _parked.TrySetCanceled(cancellationToken));
            _entered.TrySetResult();
            return _parked.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

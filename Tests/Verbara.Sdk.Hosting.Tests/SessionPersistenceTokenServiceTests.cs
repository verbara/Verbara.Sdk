using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Enums;
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

/// <summary>
/// The token every session save runs under (spec: session-persistence-lifecycle). The service hands the manager the
/// token of a source it owns, and these facts pin which phase may cancel it: the stop phase and teardown, never the start
/// phase. The manager is attached to the server by the test itself (or by the single-server attach service in a host),
/// because this service attaches nothing. Every wait ends on a signal: the pending store ends a save on the cancelling
/// thread.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SessionPersistenceTokenServiceTests : IAsyncLifetime
{
    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _server;

    public SessionPersistenceTokenServiceTests()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        _server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    // --- The start phase never cancels it ---

    [Fact]
    public async Task StartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartTokenIsCancelled()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var start = new CancellationTokenSource();
        await using var manager = CreateManager(store, logger);
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(start.Token);
        var save = TakeOneSave(manager, store);

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
    public async Task HostStartAsync_ShouldLeaveAnInFlightSaveRunning_WhenTheStartIsAborted()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateManager(store, logger);
        using var tokens = new SessionPersistenceTokenService(manager);
        using var attach = new SessionManagerHostedService(manager, _server);
        var parked = new ParkedStartHostedService();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(tokens);
                services.AddSingleton<IHostedService>(attach);
                services.AddSingleton<IHostedService>(parked);
            })
            .Build();
        using var start = new CancellationTokenSource();

        var starting = host.StartAsync(start.Token);

        // The third hosted service parks the start, so the first two are started and the host's start token is still
        // live. Waiting on the park's own signal, never on a clock.
        await Task.WhenAny(parked.Entered, starting);
        parked.Entered.IsCompletedSuccessfully.Should().BeTrue(
            "the host started the two services and then parked inside the next one");
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        start.Cancel();

        save.Token.CanBeCanceled.Should().BeTrue("premise: the save runs under the service's token");
        save.Token.IsCancellationRequested.Should().BeFalse(
            "cancelling the token passed to Host.StartAsync aborts the start, and the manager must " +
            "not be saving under it");
        save.IsObserved.Should().BeFalse("the save is still in flight");
        await FluentActions.Awaiting(() => starting).Should().ThrowAsync<OperationCanceledException>(
            "the parked hosted service ends the start as aborted");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    // --- The stop phase, as a plain IHostedService driver sees it ---

    [Fact]
    public async Task StopAsync_ShouldCutShortAnInFlightSave_WhenTheStopTokenIsCancelled()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        await using var manager = CreateManager(store, logger);
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);
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
        await using var manager = CreateManager(store, logger);
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);

        await sut.StopAsync(stop.Token);

        save.Token.IsCancellationRequested.Should().BeFalse(
            "the stop is inside its budget, so nothing may cancel the persistence token");
        save.IsObserved.Should().BeFalse(
            "a graceful stop is exactly the case in which the session state is still worth " +
            "persisting, so the save keeps its budget");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    // --- The stop phase, as a lifecycle host drives it ---

    [Fact]
    public async Task StopAsync_ShouldCancelNothing_WhenTheHostDrivesTheLifecycleAndItsStopTokenIsCancelled()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateManager(store, new RecordingLogger());
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartingAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        await sut.StoppingAsync(withdrawn.Token);
        await sut.StopAsync(withdrawn.Token);
        var afterStop = save.IsObserved;
        await sut.StoppedAsync(withdrawn.Token);

        afterStop.Should().BeFalse(
            "on a lifecycle host the wiring waits for StoppedAsync, after every hosted service's stop has returned, so "
            + "a detach another service makes in its own stop precedes the cancellation");
        save.IsObserved.Should().BeTrue("StoppedAsync wired the withdrawn stop token, which cancelled the source inline");
        save.Failure.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task HostStopAsync_ShouldCutShortAnInFlightSave_WhenTheShutdownIsNoLongerGraceful()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateManager(store, logger);
        using var tokens = new SessionPersistenceTokenService(manager);
        using var attach = new SessionManagerHostedService(manager, _server);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(tokens);
                services.AddSingleton<IHostedService>(attach);
            })
            .Build();
        await host.StartAsync(CancellationToken.None);
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        var save = store.Saves.Should().ContainSingle().Subject;

        // An already-cancelled token handed to Host.StopAsync, not HostOptions.ShutdownTimeout: the host turns that
        // option into CancelAfter on a linked source, and CancelAfter fires on a timer even at zero, so a timeout would
        // be a race. Host.StopAsync links the caller's token into the token it passes each service.
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

    // --- Teardown ---

    [Fact]
    public async Task Dispose_ShouldNotThrow_WhenCalledMoreThanOnce()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        // Disposed again at the end of the scope, a no-op: also when the start throws before the disposal under test.
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        sut.Dispose();

        // IDisposable's contract: a second Dispose must be ignored, not throw. The service owns a
        // CancellationTokenSource and Cancel on a disposed source throws ObjectDisposedException, so
        // releasing without a guard turns the second call into a fault.
        var second = sut.Dispose;
        second.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_ShouldCutShortAnInFlightSave_WhenTheServiceIsDisposedWithoutAStop()
    {
        var store = new PendingSaveStore();
        var logger = new RecordingLogger();
        await using var manager = CreateManager(store, logger);
        // Disposed again at the end of the scope, a no-op: also when the start throws before the disposal under test.
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);

        sut.Dispose();

        save.IsObserved.Should().BeTrue(
            "teardown cancelled the source before releasing it, so the save ended rather than being " +
            "left under a token nothing can ever cancel");
        save.Failure.Should().BeOfType<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task StartAsync_ShouldThrowAndHandNoToken_WhenTheServiceWasDisposed()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateManager(store, new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        sut.Dispose();

        var starting = await Record.ExceptionAsync(() => sut.StartingAsync(CancellationToken.None));
        var start = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));
        var save = TakeOneSave(manager, store);

        starting.Should().BeOfType<ObjectDisposedException>("a disposed service cannot be started again");
        start.Should().BeOfType<ObjectDisposedException>("a disposed service cannot be started again");
        save.Token.CanBeCanceled.Should().BeFalse("the refused start handed the manager no token of a released source");
    }

    // --- A stop after disposal (spec: hosted-service-lifetime) ---
    //
    // The host may stop a service it has already disposed (a failed start disposes the provider and then stops, and a
    // consumer may stop in any order). A stop after disposal does nothing, and nothing it leaves behind can throw later.
    // Each order is driven by the test itself, never by a clock.

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenTheServiceWasDisposedAndTheStopTokenIsAlreadyCancelled()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        sut.Dispose();
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        var error = await Record.ExceptionAsync(() => sut.StopAsync(withdrawn.Token));

        error.Should().BeNull(
            "a stop after disposal is a no-op: it must not wire the stop token onto the source the disposal released, "
            + "and an already-cancelled token would run that wiring inline, cancelling the released source");
    }

    [Fact]
    public async Task StoppedAsync_ShouldNotThrow_WhenTheServiceWasDisposedAndTheStopTokenIsAlreadyCancelled()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartingAsync(CancellationToken.None);
        sut.Dispose();
        using var withdrawn = new CancellationTokenSource();
        await withdrawn.CancelAsync();

        var error = await Record.ExceptionAsync(() => sut.StoppedAsync(withdrawn.Token));

        error.Should().BeNull(
            "the lifecycle host's wiring after disposal is a no-op too, so the already-cancelled token reaches no "
            + "released source");
    }

    [Fact]
    public async Task StopTokenCancel_ShouldNotThrow_WhenTheStopCameAfterTheDisposal()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        using var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        sut.Dispose();
        using var stop = new CancellationTokenSource();
        var stopError = await Record.ExceptionAsync(() => sut.StopAsync(stop.Token));

        var cancelError = Record.Exception(() => stop.Cancel());

        new Dictionary<string, Exception?> { ["Stop"] = stopError, ["Cancel"] = cancelError }
            .Where(step => step.Value is not null).Should().BeEmpty(
            "a stop after disposal leaves nothing registered on the host's token, so the host cancelling that token "
            + "later reaches no released source");
    }

    [Fact]
    public async Task StopAndDispose_ShouldNotThrowNorLeaveACancelThatThrows_WhenTheStopComesFirst()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();

        var errors = new Dictionary<string, Exception?>
        {
            ["Stop"] = await Record.ExceptionAsync(() => sut.StopAsync(stop.Token)),
            ["Dispose"] = Record.Exception(sut.Dispose),
            ["Cancel"] = Record.Exception(() => stop.Cancel()),
        };

        errors.Where(step => step.Value is not null).Should().BeEmpty(
            "stop, then dispose, then the host cancels its stop token: the disposal released the registration with "
            + "the source, so the cancel reaches nothing");
    }

    [Fact]
    public async Task StopAndDispose_ShouldNotThrowNorLeaveACancelThatThrows_WhenTheDisposalComesFirst()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();

        var errors = new Dictionary<string, Exception?>
        {
            ["Dispose"] = Record.Exception(sut.Dispose),
            ["Stop"] = await Record.ExceptionAsync(() => sut.StopAsync(stop.Token)),
            ["Cancel"] = Record.Exception(() => stop.Cancel()),
        };

        errors.Where(step => step.Value is not null).Should().BeEmpty(
            "dispose, then stop, then the host cancels its stop token: the stop after disposal registered nothing, so "
            + "the cancel reaches no released source");
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrowNorLeaveACancelThatThrows_WhenItRunsInsideTheDisposal()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateManager(store, new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);
        using var stop = new CancellationTokenSource();
        Exception? stopError = null;
        var stopRan = false;

        // The seam inside the disposal: the disposal cancels the persistence token before it releases its source, and
        // this callback runs on that cancellation, so the stop below runs while the disposal is between the two. No
        // clock decides the interleaving.
        save.Token.Register(() =>
        {
            stopRan = true;
            stopError = Record.Exception(() => sut.StopAsync(stop.Token).GetAwaiter().GetResult());
        });

        var disposeError = Record.Exception(sut.Dispose);
        var cancelError = Record.Exception(() => stop.Cancel());

        stopRan.Should().BeTrue("premise: the disposal cancelled the persistence token, so the stop ran inside it");
        new Dictionary<string, Exception?> { ["Dispose"] = disposeError, ["Stop"] = stopError, ["Cancel"] = cancelError }
            .Where(step => step.Value is not null).Should().BeEmpty(
            "a stop that runs while the disposal is releasing the source registers nothing on it, so the host "
            + "cancelling its stop token afterwards reaches no released source");
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenItsTokenIsCancelledInsideTheDisposal()
    {
        var store = new PendingSaveStore();
        await using var manager = CreateManager(store, new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        var save = TakeOneSave(manager, store);
        using var stop = new CancellationTokenSource();
        Exception? seamError = null;
        var seamRan = false;

        // The same seam, and this time the stop's own token is cancelled inside it, after the stop wired it.
        save.Token.Register(() =>
        {
            seamRan = true;
            seamError = Record.Exception(() =>
            {
                sut.StopAsync(stop.Token).GetAwaiter().GetResult();
                stop.Cancel();
            });
        });

        var disposeError = Record.Exception(sut.Dispose);
        var laterCancelError = Record.Exception(() => stop.Cancel());

        seamRan.Should().BeTrue("premise: the disposal cancelled the persistence token, so the seam ran inside it");
        new Dictionary<string, Exception?> { ["Dispose"] = disposeError, ["Seam"] = seamError, ["LaterCancel"] = laterCancelError }
            .Where(step => step.Value is not null).Should().BeEmpty(
            "a stop token cancelled while the disposal is releasing the source throws nowhere: not from the stop, not "
            + "from the disposal, not from a later cancel");
    }

    [Fact]
    public async Task StopTokenCancel_ShouldNotThrow_WhenAnEarlierStopsTokenIsCancelledAfterASecondStopAndTheDisposal()
    {
        await using var manager = CreateManager(new PendingSaveStore(), new RecordingLogger());
        var sut = new SessionPersistenceTokenService(manager);
        await sut.StartAsync(CancellationToken.None);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var errors = new Dictionary<string, Exception?>
        {
            ["FirstStop"] = await Record.ExceptionAsync(() => sut.StopAsync(first.Token)),
            ["SecondStop"] = await Record.ExceptionAsync(() => sut.StopAsync(second.Token)),
            ["Dispose"] = Record.Exception(sut.Dispose),
            ["FirstCancel"] = Record.Exception(() => first.Cancel()),
            ["SecondCancel"] = Record.Exception(() => second.Cancel()),
        };

        errors.Where(step => step.Value is not null).Should().BeEmpty(
            "a second stop releases the first stop's registration, so the first stop token cancelled after the "
            + "disposal reaches no released source");
    }

    [Fact]
    public async Task StartAsync_ShouldNotThrow_WhenNotConcreteCallSessionManager()
    {
        using var sut = new SessionPersistenceTokenService(Substitute.For<ICallSessionManager>());

        var act = async () =>
        {
            await sut.StartingAsync(CancellationToken.None);
            await sut.StartAsync(CancellationToken.None);
            await sut.StopAsync(CancellationToken.None);
            await sut.StoppedAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync("a manager that is not the SDK's gets no token, as before");
    }

    /// <summary>Attaches the manager to the server, raises one channel and returns its one save.</summary>
    private PendingSave TakeOneSave(CallSessionManager manager, PendingSaveStore store)
    {
        manager.AttachToServer(_server, "default");
        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        return store.Saves.Should().ContainSingle("the attached server's channel opened one session").Subject;
    }

    private static CallSessionManager CreateManager(SessionStoreBase store, ILogger<CallSessionManager> logger) =>
        new(Options.Create(new SessionOptions()), logger, store);
}

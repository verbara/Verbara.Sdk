namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Queues;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// A queue's counts for a caller the queue's own timeout lets go, and for a caller that leaves by key, through a real
/// Asterisk: the functional PBX's <c>test-queue-timeout</c> and <c>test-queue-key</c>, read by the one AMI user of
/// that PBX whose read classes include <c>dialplan</c> (<c>queueexits</c>), so Asterisk sends it the
/// <c>QUEUESTATUS</c> it sets on the caller's channel. Each caller stays on the line after leaving the queue until the
/// test hangs it up, so the counts are read both before and after the hang-up.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class QueueExitCountFunctionalTests : FunctionalTestBase
{
    private const string QueueExitsUser = "queueexits";
    private const string QueueExitsSecret = "queueexitspass";

    /// <summary>Bounds every wait on Asterisk, so a dialplan that never reaches its step fails the test instead of hanging it.</summary>
    private static readonly TimeSpan StepBound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task QueueTimeout_ShouldBeCountedTimedOutAndAbandonedAndNotWaiting_BeforeAndAfterTheCallerHangsUp()
    {
        await using var rig = await Rig.StartAsync(LoggerFactory);
        var queueStatus = rig.FirstFrame(f => f.EventType == "VarSet"
            && f.RawFields?.GetValueOrDefault("Variable") == "QUEUESTATUS"
            && f.RawFields.GetValueOrDefault("Channel")?.StartsWith("Local/510@test-functional", StringComparison.Ordinal) == true);

        await rig.OriginateAsync("Local/510@test-functional", application: "Wait", data: "40");
        var status = await queueStatus.WaitAsync(StepBound);
        var beforeHangup = rig.Tally("test-queue-timeout");
        await rig.HangUpAndWaitForTheCallToEndAsync(status.RawFields!["Channel"], StepBound);

        using var scope = new AssertionScope();
        status.RawFields!.GetValueOrDefault("Value").Should().Be("TIMEOUT", "premise: the queue's own timeout let the caller go");
        beforeHangup.Should().Be(new Counts(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 1, Waiting: 0),
            "Asterisk reported the abandon, the leave and QUEUESTATUS=TIMEOUT, and the caller is still on the line");
        rig.Tally("test-queue-timeout").Should().Be(new Counts(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 1, Waiting: 0),
            "the hang-up counts nothing twice");
    }

    [Fact]
    public async Task KeyExit_ShouldBeCountedNeitherAnsweredNorAbandonedAndNotWaiting_BeforeAndAfterTheCallerHangsUp()
    {
        await using var rig = await Rig.StartAsync(LoggerFactory);
        var leave = rig.FirstFrame(f => f.EventType == "QueueCallerLeave"
            && f.RawFields?.GetValueOrDefault("Queue") == "test-queue-key");
        var abandon = rig.FirstFrame(f => f.EventType == "QueueCallerAbandon"
            && f.RawFields?.GetValueOrDefault("Queue") == "test-queue-key");

        await rig.OriginateAsync("Local/520@test-functional", context: "test-functional", exten: "521");
        var left = await leave.WaitAsync(StepBound);
        var beforeHangup = rig.Tally("test-queue-key");
        await rig.HangUpAndWaitForTheCallToEndAsync(left.RawFields!["Channel"], StepBound);

        using var scope = new AssertionScope();
        abandon.IsCompleted.Should().BeFalse("premise: Asterisk reports no abandon for a caller that leaves by key");
        beforeHangup.Should().Be(new Counts(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "the caller pressed 1: Asterisk reported the leave with no abandon, and the caller is in the exit context");
        rig.Tally("test-queue-key").Should().Be(new Counts(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0),
            "Asterisk counts a key exit neither answered nor abandoned, and the hang-up does not make it one");
    }

    /// <summary>
    /// A connection as <c>queueexits</c>, a <see cref="VerbaraServer"/> on it, and the session manager and queue tracker
    /// the hosting extensions register, attached to that server. A frame observer subscribed after the server's own
    /// sees each frame once the server has handled it: the connection hands every frame to its observers in turn.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly AmiConnection _connection;
        private readonly VerbaraServer _server;
        private readonly ServiceProvider _services;
        private readonly CallSessionManager _manager;
        private readonly IQueueSessionTracker _tracker;
        private readonly FrameWatch _frames = new();
        private readonly IDisposable _frameSubscription;

        private Rig(AmiConnection connection, VerbaraServer server, ServiceProvider services, CallSessionManager manager,
            IQueueSessionTracker tracker)
        {
            _connection = connection;
            _server = server;
            _services = services;
            _manager = manager;
            _tracker = tracker;
            _frameSubscription = connection.Subscribe(_frames);
        }

        public static async Task<Rig> StartAsync(ILoggerFactory loggerFactory)
        {
            var connection = Infrastructure.Helpers.AmiConnectionFactory.Create(loggerFactory, opts =>
            {
                opts.Username = QueueExitsUser;
                opts.Password = QueueExitsSecret;
                opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
                opts.AutoReconnect = false;
            });
            VerbaraServer? server = null;
            ServiceProvider? services = null;
            try
            {
                await connection.ConnectAsync();
                server = new VerbaraServer(connection, loggerFactory.CreateLogger<VerbaraServer>());
                await server.StartAsync();

                services = new ServiceCollection()
                    .AddSingleton(loggerFactory)
                    .AddLogging()
                    .AddVerbaraSessionsMultiServer()
                    .BuildServiceProvider();
                var manager = (CallSessionManager)services.GetRequiredService<ICallSessionManager>();
                var tracker = services.GetRequiredService<IQueueSessionTracker>();
                manager.AttachToServer(server, "queue-exits");
                return new Rig(connection, server, services, manager, tracker);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (services is not null)
                    await services.DisposeAsync();
                if (server is not null)
                    await server.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>The first frame, from now on, that <paramref name="match"/> accepts.</summary>
        public Task<ManagerEvent> FirstFrame(Func<ManagerEvent, bool> match) => _frames.Arm(match);

        public Task<ManagerResponse> OriginateAsync(string channel, string? application = null, string? data = null, string? context = null,
            string? exten = null) =>
            _connection.SendActionAsync(new OriginateAction
            {
                Channel = channel, Application = application, Data = data, Context = context, Exten = exten,
                Priority = exten is null ? null : 1, IsAsync = true, Timeout = 15000,
            }).AsTask();

        /// <summary>Hangs up <paramref name="channel"/> and waits for the call's <see cref="CallEndedEvent"/>.</summary>
        public async Task HangUpAndWaitForTheCallToEndAsync(string channel, TimeSpan bound)
        {
            var uniqueId = _server.Channels.GetByName(channel)?.UniqueId
                ?? throw new InvalidOperationException($"The server does not hold {channel}.");
            var session = _manager.GetByChannelId(uniqueId)
                ?? throw new InvalidOperationException($"The manager holds no call for {channel}.");
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = _manager.Events.Subscribe(evt =>
            {
                if (evt is CallEndedEvent && evt.SessionId == session.SessionId)
                    ended.TrySetResult();
            });

            await _connection.SendActionAsync(new HangupAction { Channel = channel, Cause = 16 });
            await ended.Task.WaitAsync(bound);
        }

        public Counts Tally(string queue) =>
            _tracker.GetByQueueName(queue) is { } q
                ? new Counts(q.CallsOffered, q.CallsAnswered, q.CallsAbandoned, q.CallsTimedOut, q.CallsWaiting)
                : new Counts(0, 0, 0, 0, 0);

        public async ValueTask DisposeAsync()
        {
            _frameSubscription.Dispose();
            await _services.DisposeAsync();
            await _server.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>Every count a queue keeps.</summary>
    private readonly record struct Counts(int Offered, int Answered, int Abandoned, int TimedOut, int Waiting);

    /// <summary>An observer that completes each armed wait with the first frame, after arming, that it accepts.</summary>
    private sealed class FrameWatch : IObserver<ManagerEvent>
    {
        private readonly Lock _gate = new();
        private readonly List<(Func<ManagerEvent, bool> Match, TaskCompletionSource<ManagerEvent> Seen)> _armed = [];

        public Task<ManagerEvent> Arm(Func<ManagerEvent, bool> match)
        {
            var seen = new TaskCompletionSource<ManagerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _armed.Add((match, seen));
            }

            return seen.Task;
        }

        public void OnNext(ManagerEvent value)
        {
            lock (_gate)
            {
                foreach (var (match, seen) in _armed)
                {
                    if (!seen.Task.IsCompleted && match(value))
                        seen.TrySetResult(value);
                }
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}

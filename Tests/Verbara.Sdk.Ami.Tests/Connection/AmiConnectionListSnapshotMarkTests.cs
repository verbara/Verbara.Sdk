using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// Where a list's sequence ended in the session's event stream: the connection records the ordinal of the list's
/// <c>…Complete</c> event in the session's pump, or, for a list Asterisk refused, of the last event received before the
/// refusal, and a wait on that mark ends only once the pump has dispatched every event received up to it. Driven over a
/// piped peer; no wall clock is read except the hang bound.
/// </summary>
public sealed class AmiConnectionListSnapshotMarkTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldMarkTheOrdinalTheCompleteEventCarriesInThePump_WhenTheListCompletes()
    {
        await using var rig = await Rig.StartAsync(holdOn: null);

        var mark = new ListSnapshotMark();
        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction(), null, mark));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.WriteEventAsync("Hangup", [new("Channel", "PJSIP/a-1"), new("Uniqueid", "1.1"), new("Cause", "16")]);
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        await rig.Peer.WriteEventAsync("Status", [new("ActionID", id), new("Channel", "PJSIP/b-1"), new("Uniqueid", "2.1")]);
        await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "1")]);
        var received = await read.WaitAsync(Bound);
        var outcome = await mark.WaitDispatchedThroughAsync(Bound, TimeProvider.System).AsTask().WaitAsync(Bound * 2);

        using (new AssertionScope())
        {
            received.Select(e => e.EventType).Should().Equal(["Status"], "the list carries its one entry");
            mark.Pump.Should().NotBeNull("the list completed on a live session");
            mark.CompleteOrdinal.Should().Be(rig.DispatchOrdinalOf("StatusComplete"),
                "the mark is the position the pump dispatched the StatusComplete at");
            outcome.Should().Be(DispatchBarrierOutcome.Reached);
            rig.Dispatched.Should().Equal(["Hangup", "Status", "StatusComplete"],
                "by the time the wait ends, every event received up to the StatusComplete was dispatched, in order");
        }
    }

    [Fact]
    public async Task ListSnapshotMarkWait_ShouldEndOnlyOnceTheEarlierEventIsDispatched_WhenItsHandlerIsHeldAsTheListCompletes()
    {
        await using var rig = await Rig.StartAsync(holdOn: "Hangup");

        var mark = new ListSnapshotMark();
        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction(), null, mark));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.WriteEventAsync("Hangup", [new("Channel", "PJSIP/a-1"), new("Uniqueid", "1.1"), new("Cause", "17")]);
        await rig.Entered.WaitAsync(Bound);
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "0")]);
        var received = await read.WaitAsync(Bound);

        var wait = mark.WaitDispatchedThroughAsync(Bound, TimeProvider.System).AsTask();
        var completedWhileHeld = wait.IsCompleted;
        var throughWhileHeld = mark.Pump?.DispatchedThrough;
        rig.Release();
        var outcome = await wait.WaitAsync(Bound * 2);

        using (new AssertionScope())
        {
            received.Should().BeEmpty("the peer's list was empty");
            mark.CompleteOrdinal.Should().BeGreaterThan(0, "the list completed");
            completedWhileHeld.Should().BeFalse("the Hangup received before the StatusComplete is still in its handler");
            throughWhileHeld.Should().BeLessThan(mark.CompleteOrdinal);
            outcome.Should().Be(DispatchBarrierOutcome.Reached);
            rig.Dispatched.Should().Equal(["Hangup", "StatusComplete"]);
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldMarkTheLastEventReceivedBeforeTheRefusal_WhenAsteriskRefusesTheList()
    {
        // A conference listing with no conference up is answered 'Response: Error' / 'No active conferences.': the answer
        // is a list with no entries, and the events received before it still have to be dispatched before it is applied.
        await using var rig = await Rig.StartAsync(holdOn: "Hangup");

        var mark = new ListSnapshotMark();
        var outcomeOfAction = new EventActionOutcome();
        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction(), outcomeOfAction, mark));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.WriteEventAsync("Hangup", [new("Channel", "PJSIP/a-1"), new("Uniqueid", "1.1"), new("Cause", "16")]);
        await rig.Entered.WaitAsync(Bound);
        await rig.Peer.RespondAsync("Error", id, [new("Message", "No active conferences.")]);
        var received = await read.WaitAsync(Bound);

        var wait = mark.WaitDispatchedThroughAsync(Bound, TimeProvider.System).AsTask();
        var completedWhileHeld = wait.IsCompleted;
        rig.Release();
        var outcome = await wait.WaitAsync(Bound * 2);

        using (new AssertionScope())
        {
            received.Should().BeEmpty();
            outcomeOfAction.Rejection.Should().Be("No active conferences.");
            mark.Pump.Should().NotBeNull("the refusal arrived on a live session");
            mark.CompleteOrdinal.Should().Be(1, "the Hangup, the one event received before the refusal, is its mark");
            completedWhileHeld.Should().BeFalse("the Hangup received before the refusal is still in its handler");
            outcome.Should().Be(DispatchBarrierOutcome.Reached);
            rig.Dispatched.Should().Equal(["Hangup"]);
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldLeaveNothingToWaitFor_WhenAsteriskRefusesTheListBeforeAnyEvent()
    {
        await using var rig = await Rig.StartAsync(holdOn: null);

        var mark = new ListSnapshotMark();
        var outcomeOfAction = new EventActionOutcome();
        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction(), outcomeOfAction, mark));
        var id = await rig.ReadActionIdAsync();
        await rig.Peer.RespondAsync("Error", id, [new("Message", "Permission denied")]);
        var received = await read.WaitAsync(Bound);
        var wait = mark.WaitDispatchedThroughAsync(Timeout.InfiniteTimeSpan, TimeProvider.System);

        using (new AssertionScope())
        {
            received.Should().BeEmpty();
            outcomeOfAction.Rejection.Should().Be("Permission denied");
            mark.CompleteOrdinal.Should().Be(0, "no event was received before the refusal");
            wait.IsCompleted.Should().BeTrue("a mark before the first event has nothing to wait for");
            (await wait).Should().Be(DispatchBarrierOutcome.Reached);
        }
    }

    private static async Task<List<ManagerEvent>> ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);

        return received;
    }

    /// <summary>A logged-in connection over a piped peer, with an <c>OnEvent</c> handler that records every dispatch
    /// and can hold the dispatch of one event type until <see cref="Release"/>.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _peerCts = new(Bound * 3);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _dispatched = [];
        private readonly Lock _sync = new();
        private readonly string? _holdOn;

        private Rig(string? holdOn) => _holdOn = holdOn;

        public AmiConnection Connection { get; private set; } = null!;

        public PipedSocket Peer { get; private set; } = null!;

        public Task Entered => _entered.Task;

        public List<string> Dispatched
        {
            get
            {
                lock (_sync)
                    return [.. _dispatched];
            }
        }

        /// <summary>The 1-based position at which the handler saw the first event of <paramref name="eventType"/>; 0 if none.</summary>
        public long DispatchOrdinalOf(string eventType)
        {
            lock (_sync)
                return _dispatched.IndexOf(eventType) + 1;
        }

        public void Release() => _gate.TrySetResult();

        public static async Task<Rig> StartAsync(string? holdOn)
        {
            var rig = new Rig(holdOn);
            var factory = new PipedSocketFactory();
            rig.Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = false,
                DefaultEventTimeout = TimeSpan.Zero,
            }), factory, NullLogger<AmiConnection>.Instance);
            rig.Connection.OnEvent += rig.OnEventAsync;
            var loggedIn = Task.Run(async () =>
            {
                var peer = await factory.NextAsync(rig._peerCts.Token);
                await peer.CompleteLoginAsync(rig._peerCts.Token);
                return peer;
            }, rig._peerCts.Token);
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            rig.Peer = await loggedIn.WaitAsync(Bound);
            return rig;
        }

        public async Task<string> ReadActionIdAsync()
        {
            var action = await Peer.ReadActionAsync(_peerCts.Token).WaitAsync(Bound)
                ?? throw new InvalidOperationException("The session ended before the connection sent its action.");
            return PipedSocket.ActionIdOf(action);
        }

        private async ValueTask OnEventAsync(ManagerEvent evt)
        {
            if (evt.EventType == _holdOn)
            {
                _entered.TrySetResult();
                await _gate.Task;
            }

            lock (_sync)
                _dispatched.Add(evt.EventType!);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await Connection.DisposeAsync();
            _peerCts.Dispose();
        }
    }
}

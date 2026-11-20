using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The ways an agent or bridge event reaches its subscribers through a server: a live AMI event, the start's load and a
/// reconnect's reload (the bridge reconciliation a reload runs is driven directly in <c>BridgeSubscriberIsolationTests</c>:
/// over a connection that is not an <c>AmiConnection</c> a reload removes no bridge). On each, a subscriber that throws is counted on <c>live.agents.subscriber_faults</c> or
/// <c>live.bridges.subscriber_faults</c>, the other subscribers, the events that follow in the same report and the
/// server's own work go on, and nothing leaves the call: not the AMI observer, not <c>StartAsync</c>, not the reload.
/// The counters are process-wide, so the class runs in <see cref="LiveCountersGroup"/>.
/// </summary>
[Collection(LiveCountersGroup.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AgentAndBridgeSubscriberFaultPathTests : IAsyncLifetime
{
    private const string AgentFaults = "live.agents.subscriber_faults";
    private const string BridgeFaults = "live.bridges.subscriber_faults";
    private const string ObserverFaults = "ami.events.observer_faults";
    private const string Queue = "sales";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly LoadLogger _log = new();
    private readonly VerbaraServer _server;
    private IObserver<ManagerEvent>? _observer;
    private ManagerEvent[] _agents = [];
    private ManagerEvent[] _queueStatus = [];

    public AgentAndBridgeSubscriberFaultPathTests()
    {
        _connection.AsteriskVersion.Returns("22.0.0");
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs)).Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Answer(ci.ArgAt<ManagerAction>(0) switch
            {
                AgentsAction => _agents,
                QueueStatusAction => _queueStatus,
                _ => [],
            }));
        _server = new VerbaraServer(_connection, _log);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task EventObserver_ShouldRaiseAgentConnectedAndTheQueueCallerReport_WhenAnAgentStateChangedHandlerThrows()
    {
        await _server.StartAsync();
        _observer!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        var stateHeard = 0;
        var connected = new List<string>();
        var callerConnected = new List<string?>();
        _server.Agents.AgentStateChanged += _ => throw new InvalidOperationException("agent-state-handler");
        _server.Agents.AgentStateChanged += _ => stateHeard++;
        _server.Agents.AgentConnected += (agentId, _, _) => connected.Add(agentId);
        _server.Agents.QueueCallerConnected += (callerUniqueId, _, _) => callerConnected.Add(callerUniqueId);
        using var faults = new Faults();

        var thrown = Record.Exception(() => _observer.OnNext(new AgentConnectEvent
        {
            Agent = "1001", Channel = "PJSIP/caller-1", UniqueId = "caller-u1", LinkedId = "linked-1", Interface = "Agent/1001",
        }));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("an AgentStateChanged subscriber's failure no longer reaches the AMI event observer");
        stateHeard.Should().Be(1, "the subscriber after the throwing one hears the state change");
        connected.Should().Equal(["1001"], "the AgentConnected that follows the state change is raised");
        callerConnected.Should().Equal(["caller-u1"], "the server's own queue-caller report after it is raised");
        faults.Count(AgentFaults).Should().Be(1);
        faults.Count(ObserverFaults).Should().Be(0, "it is the agent subscriber's failure, not the AMI observer's");
    }

    [Fact]
    public async Task EventObserver_ShouldLinkTheBridgedChannels_WhenAChannelEnteredHandlerThrows()
    {
        await _server.StartAsync();
        _observer!.OnNext(new NewChannelEvent { UniqueId = "ch.1", Channel = "PJSIP/2000-001", ChannelState = "Up" });
        _observer.OnNext(new NewChannelEvent { UniqueId = "ch.2", Channel = "PJSIP/3000-001", ChannelState = "Up" });
        _observer.OnNext(new BridgeCreateEvent { BridgeUniqueid = "bridge-1", BridgeType = "basic", BridgeTechnology = "simple_bridge" });
        _observer.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.1" });
        var entered = new List<string>();
        _server.Bridges.ChannelEntered += (_, _) => throw new InvalidOperationException("channel-entered-handler");
        _server.Bridges.ChannelEntered += (_, uniqueId) => entered.Add(uniqueId);
        using var faults = new Faults();

        var thrown = Record.Exception(() => _observer.OnNext(new BridgeEnterEvent { BridgeUniqueid = "bridge-1", UniqueId = "ch.2" }));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a ChannelEntered subscriber's failure no longer reaches the AMI event observer");
        entered.Should().Equal(["ch.2"], "the subscriber after the throwing one hears the entry");
        _server.Channels.GetByUniqueId("ch.1")!.LinkedChannel.Should().BeSameAs(_server.Channels.GetByUniqueId("ch.2"),
            "the server links the two channels of the bridge after raising the entry");
        _server.Channels.GetByUniqueId("ch.2")!.LinkedChannel.Should().BeSameAs(_server.Channels.GetByUniqueId("ch.1"));
        faults.Count(BridgeFaults).Should().Be(1);
        faults.Count(ObserverFaults).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldCompleteWithEveryAgentLoggedIn_WhenAnAgentLoggedInHandlerThrowsDuringTheLoad()
    {
        _agents =
        [
            new AgentsEvent { Agent = "1001", Status = "AGENT_IDLE", LoggedInChan = "PJSIP/1001" },
            new AgentsEvent { Agent = "2001", Status = "AGENT_IDLE", LoggedInChan = "PJSIP/2001" },
        ];
        var announced = 0;
        _server.Agents.AgentLoggedIn += _ =>
        {
            announced++;
            throw new InvalidOperationException("agent-logged-in-handler");
        };
        using var faults = new Faults();

        var thrown = await Record.ExceptionAsync(() => _server.StartAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a subscriber's failure during the load does not fail the start");
        announced.Should().Be(2, "both agents the list reports are announced");
        _server.Agents.Agents.Select(a => a.AgentId).Should().BeEquivalentTo(["1001", "2001"]);
        faults.Count(AgentFaults).Should().Be(2);
    }

    [Fact]
    public async Task Reconnect_ShouldAnnounceEveryAgentAndCompleteTheReload_WhenAnAgentLoggedInHandlerThrows()
    {
        await _server.StartAsync();
        _queueStatus = [new QueueParamsEvent { Queue = Queue, Max = 0, Strategy = "ringall", Calls = 0 }];
        _agents =
        [
            new AgentsEvent { Agent = "1001", Status = "AGENT_IDLE", LoggedInChan = "PJSIP/1001" },
            new AgentsEvent { Agent = "2001", Status = "AGENT_IDLE", LoggedInChan = "PJSIP/2001" },
        ];
        var announced = new ConcurrentQueue<string>();
        _server.Agents.AgentLoggedIn += _ => throw new InvalidOperationException("agent-logged-in-handler");
        _server.Agents.AgentLoggedIn += a => announced.Enqueue(a.AgentId);
        using var faults = new Faults();

        var ended = await ReconnectAsync();

        using var scope = new AssertionScope();
        ended.Should().Be(LoadLogger.Loaded, $"the reload completes past a throwing subscriber. Server log:{Environment.NewLine}{_log}");
        announced.Should().Equal(["1001", "2001"], "every agent the list reports logged in is announced");
        _server.Agents.Agents.Select(a => a.AgentId).Should().BeEquivalentTo(["1001", "2001"]);
        _server.Queues.GetByName(Queue).Should().NotBeNull("the queues the reload listed are loaded");
        faults.Count(AgentFaults).Should().Be(2);
    }

    // Raises Reconnected and waits for the reload's end: its "State loaded" line or its "Reconnect reload failed" line.
    private async Task<string> ReconnectAsync()
    {
        _log.Rearm();
        _connection.Reconnected += Raise.Event<Action>();
        // fence-allow: GUARD-TIMEOUT — bounds the wait so a reload that never ends reports the log instead of hanging
        var timeout = Task.Delay(TimeSpan.FromSeconds(10));
        var finished = await Task.WhenAny(_log.Ended.Task, timeout);
        return ReferenceEquals(finished, timeout) ? "timed out" : await _log.Ended.Task;
    }

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] answer)
    {
        await Task.Yield();
        foreach (var evt in answer)
            yield return evt;
    }

    /// <summary>The server's log, and the end of the next load: <see cref="Loaded"/> or <see cref="Failed"/>.</summary>
    private sealed class LoadLogger : ILogger<VerbaraServer>
    {
        public const string Loaded = "loaded";
        public const string Failed = "failed";
        private readonly ConcurrentQueue<string> _lines = new();

        public TaskCompletionSource<string> Ended { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Rearm() => Ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            _lines.Enqueue($"{logLevel}: {line}{(exception is null ? "" : " — " + exception.Message)}");
            if (line.StartsWith("[LIVE] State loaded", StringComparison.Ordinal))
                Ended.TrySetResult(Loaded);
            else if (line.StartsWith("[LIVE] Reconnect reload failed", StringComparison.Ordinal))
                Ended.TrySetResult(Failed);
        }

        public override string ToString() => "  " + string.Join(Environment.NewLine + "  ", _lines);
    }

    /// <summary>Sums the named counters across threads: the class runs alone, in <see cref="LiveCountersGroup"/>.</summary>
    private sealed class Faults : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Faults()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is AgentFaults or BridgeFaults or ObserverFaults)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                _counts.AddOrUpdate(instrument.Name, value, (_, current) => current + value));
            _listener.Start();
        }

        public long Count(string name) => _counts.GetValueOrDefault(name);

        public void Dispose() => _listener.Dispose();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds the **initial** load: what a first <c>VerbaraServer.StartAsync</c> does to an empty channel
/// table. <c>RequestInitialStateAsync</c> serves both the first load and the post-reconnect reload,
/// so turning the reload into a diff (this change's Phase B) moves the code every startup runs. A
/// diff against an empty table must degenerate to "everything is added" — exactly today's
/// behaviour. These tests pass against the unfixed code, so a Phase B break in startup is
/// attributable to Phase B rather than argued about.
///
/// <para>Two traps this harness inherits from <see cref="ReconnectReloadTests"/>, which is the
/// reference for the fake connection: the status reply must be armed <em>before</em>
/// <c>StartAsync</c>, because <c>StartAsync</c> ends with its own <c>RequestInitialStateAsync</c>;
/// and a server that was only constructed has subscribed to nothing. Unlike the reconnect path
/// (<c>async void OnReconnected</c>, every exception swallowed into a log line), the initial load is
/// awaitable — <c>await StartAsync()</c> is the completion signal, so no completion source is
/// needed and a failure surfaces as a thrown exception rather than as a timeout.</para>
///
/// <para>Deliberately <em>not</em> asserted: the <c>LinkedId</c> the loaded channels carry, and the
/// session identity derived from it. Task 2.3 of this change passes <c>StatusEvent.LinkedId</c>
/// through on purpose, so binding today's value here would bind the defect and fail for the right
/// change. What is bound is what must not move: which channels are held, that each one is announced
/// exactly once through <c>ChannelAdded</c>, that nothing is announced as removed, and that the
/// announcement reaches the session manager.</para>
/// </summary>
public sealed class InitialLoadTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;

    private readonly List<string> _actionsSeen = [];
    private readonly List<AsteriskChannel> _added = [];
    private readonly List<AsteriskChannel> _removed = [];
    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public InitialLoadTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));

        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sessions = new CallSessionManager(
            Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());

        _sessions.AttachToServer(_server, ServerId);
        _sessionSubscription = _sessions.Events.Subscribe(_sessionEvents.Add);

        // Subscribed before StartAsync: the initial load raises ChannelAdded from inside it, so a
        // handler attached afterwards would see an empty list and the test would pass vacuously.
        _server.Channels.ChannelAdded += _added.Add;
        _server.Channels.ChannelRemoved += _removed.Add;
    }

    /// <summary>
    /// Answers the three actions <c>RequestInitialStateAsync</c> sends. Only the channel leg carries
    /// data; the queue and agent legs answer empty, which is what an idle estate returns.
    /// </summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();
        lock (_actionsSeen)
        {
            _actionsSeen.Add(action.GetType().Name);
        }

        if (action is StatusAction)
        {
            foreach (var status in _statusReply)
            {
                yield return status;
            }
        }
    }

    /// <summary>
    /// Arms the snapshot and runs the first load. The arming happens here, before
    /// <c>StartAsync</c>, because <c>StartAsync</c> runs its own <c>RequestInitialStateAsync</c> —
    /// a reply armed after it would describe a load that already happened against nothing.
    /// </summary>
    private async Task WhenTheServerStartsAndAsteriskReports(params StatusEvent[] channelsAsteriskHas)
    {
        _statusReply = channelsAsteriskHas;
        _server.Channels.ChannelCount.Should().Be(0, "the initial load starts from an empty table");

        await _server.StartAsync();
    }

    /// <summary>The measurement in one line, so a failure reports the table and not a dump.</summary>
    private string Describe() =>
        $"{_server.Channels.ChannelCount} channel(s) held [" +
        string.Join(" | ", _server.Channels.ActiveChannels.Select(
            c => $"uid={c.UniqueId} name={c.Name} state={c.State}")) +
        $"]; ChannelAdded raised for [{string.Join(", ", _added.Select(c => c.UniqueId))}]" +
        $"; ChannelRemoved raised for [{string.Join(", ", _removed.Select(c => c.UniqueId))}]";

    /// <summary>
    /// A channel as <c>Status</c> reports it. <c>Context</c> only ever reaches the manager through
    /// <c>RawFields</c> — <see cref="StatusEvent"/> has no typed property for it — so a reload that
    /// stopped reading the raw field would lose the direction the session manager infers from it.
    /// </summary>
    private static StatusEvent Leg(
        string uniqueId, string channel, string linkedId,
        ChannelState state = ChannelState.Up,
        string? callerId = null, string? context = null, string? extension = null) => new()
        {
            UniqueId = uniqueId,
            Channel = channel,
            State = state.ToString(),
            LinkedId = linkedId,
            CallerId = callerId,
            Extension = extension,
            RawFields = context is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["Context"] = context },
        };

    [Fact]
    public async Task StartAsync_ShouldHoldExactlyTheChannelsTheSnapshotContains_WhenTheTableIsEmpty()
    {
        await WhenTheServerStartsAndAsteriskReports(
            Leg("caller-100", "PJSIP/trunk-100", "linked-100", ChannelState.Up,
                callerId: "5551234", context: "from-trunk", extension: "800"),
            Leg("agent-100", "PJSIP/100-100", "linked-100"),
            Leg("solo-200", "PJSIP/201-200", "solo-200", ChannelState.Ring));

        _server.Channels.ActiveChannels.Select(c => c.UniqueId).Should().BeEquivalentTo(
            ["caller-100", "agent-100", "solo-200"],
            "the first load holds exactly what Asterisk reported — no channel invented, none "
            + $"dropped, none held twice. Measured: {Describe()}");

        var caller = _server.Channels.GetByUniqueId("caller-100");
        caller.Should().NotBeNull();
        caller!.Name.Should().Be("PJSIP/trunk-100");
        caller.State.Should().Be(ChannelState.Up,
            "Status reports the state as a name and the load parses it; an unparsed state would "
            + "silently become Unknown");
        caller.CallerIdNum.Should().Be("5551234",
            "the status event's CallerId is what the load passes as callerIdNum");
        caller.Context.Should().Be("from-trunk",
            "Context arrives only in RawFields, and the session manager infers call direction from it");
        caller.Extension.Should().Be("800");

        _server.Channels.GetByName("PJSIP/trunk-100").Should().BeSameAs(caller,
            "the name index is the second half of the table; a load that fills only the id index "
            + "breaks every GetByName lookup a consumer makes");

        _server.Channels.GetByUniqueId("solo-200")!.State.Should().Be(ChannelState.Ring,
            "a channel that is still ringing at load time is held in the state it is in");
    }

    [Fact]
    public async Task StartAsync_ShouldAnnounceEveryLoadedChannelOnceAndRemoveNothing_WhenTheTableIsEmpty()
    {
        await WhenTheServerStartsAndAsteriskReports(
            Leg("caller-100", "PJSIP/trunk-100", "linked-100"),
            Leg("agent-100", "PJSIP/100-100", "linked-100"),
            Leg("solo-200", "PJSIP/201-200", "solo-200"));

        _added.Select(c => c.UniqueId).Should().BeEquivalentTo(
            ["caller-100", "agent-100", "solo-200"],
            "ChannelAdded is the only way a consumer learns a channel exists; against an empty "
            + "table every channel in the snapshot is new, and none is announced twice. Measured: "
            + Describe());

        _removed.Should().BeEmpty(
            "nothing was held before the first load, so nothing can have gone away. A removal here "
            + $"would reach CallSessionManager and end a call that never started. Measured: {Describe()}");

        _added.Should().OnlyContain(
            c => ReferenceEquals(c, _server.Channels.GetByUniqueId(c.UniqueId)),
            "the announced channel is the one the table holds — a consumer that keeps the event's "
            + "object and the table's must not be holding two different channels");

        List<string> actions;
        lock (_actionsSeen)
        {
            actions = [.. _actionsSeen];
        }

        actions.Should().Equal(
            [nameof(StatusAction), nameof(QueueStatusAction), nameof(AgentsAction)],
            "the load sends the three snapshot actions in this order; a load that skipped one "
            + "would leave a manager empty while this test still saw its channels");
    }

    [Fact]
    public async Task StartAsync_ShouldOpenOneSessionPerLoadedCall_WhenTheTableIsEmpty()
    {
        // One leg, whose Linkedid is its own Uniqueid — what Asterisk reports for the channel that
        // started the call. Held to one leg on purpose: the number of sessions two correlated legs
        // produce is what task 2.3 changes, so asserting it here would bind the defect.
        await WhenTheServerStartsAndAsteriskReports(
            Leg("solo-200", "PJSIP/201-200", "solo-200", ChannelState.Up, context: "from-internal"));

        var session = _sessions.ActiveSessions.Should().ContainSingle(
            "the load's ChannelAdded has to reach the session manager, or a consumer sees no calls "
            + $"at all after a restart. Measured: {Describe()}").Subject;

        session.LinkedId.Should().Be("solo-200",
            "a single-leg call is keyed on its own id whether the load passes Linkedid or falls "
            + "back to the UniqueId, so this identity survives task 2.3");
        session.Participants.Should().ContainSingle()
            .Which.UniqueId.Should().Be("solo-200");

        _sessionEvents.OfType<CallStartedEvent>().Should().ContainSingle(
            "the consumer opens its own record on CallStartedEvent. Measured: "
            + $"{_sessionEvents.Count} domain event(s): "
            + string.Join(", ", _sessionEvents.Select(e => e.GetType().Name)));
    }

    [Fact]
    public async Task StartAsync_ShouldHoldNothingAndAnnounceNothing_WhenAsteriskReportsNoChannels()
    {
        await WhenTheServerStartsAndAsteriskReports();

        _server.Channels.ChannelCount.Should().Be(0,
            $"an idle estate reports no channels and the load invents none. Measured: {Describe()}");
        _added.Should().BeEmpty();
        _removed.Should().BeEmpty(
            "an empty snapshot over an empty table is a difference of nothing; a diff that raises "
            + $"a removal here raises it for a channel that never existed. Measured: {Describe()}");
        _sessions.ActiveSessions.Should().BeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        _sessionSubscription.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }
}

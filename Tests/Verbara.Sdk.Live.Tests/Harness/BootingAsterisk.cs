using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Sdk.Ami.Tests.Connection;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>Where, in a session's connect, a peer that has already booted sends <c>FullyBooted</c>.</summary>
public enum FullyBootedPlacement
{
    /// <summary>Once the connect has been answered: after the version probe's response.</summary>
    AfterConnect,

    /// <summary>
    /// Right after the login's response and before the version probe's response: the order an Asterisk that
    /// has already started uses, while the connection is still reading its connect's responses.
    /// </summary>
    AfterLoginResponse,

    /// <summary>Before the login's response, while the connection is still waiting for it.</summary>
    BeforeLoginResponse,
}

/// <summary>When the peer ends the session on its own.</summary>
public enum PeerClose
{
    /// <summary>Only when the test calls <see cref="BootingAsterisk.CloseSession"/>, or the run ends.</summary>
    Never,

    /// <summary>
    /// Right after it has answered the connect (the login and the version probe), before it reads anything else.
    /// </summary>
    AfterConnect,

    /// <summary>Right after it has answered its first refusal.</summary>
    AfterFirstRefusal,

    /// <summary>When it reads the action <see cref="BootingAsterisk.CloseWhenAsked"/> names, which it leaves unanswered.</summary>
    WhenAsked,

    /// <summary>
    /// In the middle of a <c>Status</c> list: after <see cref="BootingAsterisk.StatusChannelsBeforeClose"/> of its
    /// channels, before <c>StatusComplete</c>.
    /// </summary>
    DuringStatus,
}

/// <summary>A channel the peer lists on <c>Status</c>, with the headers Asterisk sends for it.</summary>
/// <param name="ChannelState">Asterisk's numeric channel state; 6 is <c>Up</c>.</param>
/// <param name="BridgeId">
/// The <c>BridgeID</c> header: the id of the bridge the channel is in, which Asterisk 22.12, 23.6 and 24.0 rc2 send equal
/// to <c>BridgeList</c>'s <c>BridgeUniqueid</c>. <see langword="null"/> writes no header, as before.
/// </param>
internal sealed record StatusChannel(string UniqueId, string Channel, string LinkedId, int ChannelState = 6,
    string? BridgeId = null);

/// <summary>A bridge the peer lists on <c>BridgeList</c>, one <c>BridgeListItem</c> with the headers Asterisk sends.</summary>
/// <param name="Type">
/// <c>BridgeType</c>: <c>basic</c> for a two-party bridge (<c>simple_bridge</c>), <c>base</c> for a conference
/// (<c>softmix</c>) or holding (<c>holding_bridge</c>) bridge.
/// </param>
internal sealed record ListedBridge(string Id, int NumChannels, string Type = "basic",
    string Technology = "simple_bridge", string Creator = "<unknown>", string Name = "<unknown>");

/// <summary>A participant the peer lists in a room on <c>ConfbridgeList</c>.</summary>
internal sealed record ListedParticipant(string Channel, string UniqueId, string LinkedId, bool Muted = false,
    bool Talking = false);

/// <summary>A ConfBridge room the peer lists on <c>ConfbridgeListRooms</c>, with its participants.</summary>
internal sealed record ListedRoom(string Conference, IReadOnlyList<ListedParticipant> Participants);

/// <summary>How the peer answers the two conference listings.</summary>
public enum ConferenceListing
{
    /// <summary>
    /// The rooms of <see cref="BootingAsterisk.Rooms"/>; with none, <c>Response: Error</c> and
    /// <c>Message: No active conferences.</c>, which is how Asterisk answers an empty listing.
    /// </summary>
    Listed,

    /// <summary><c>Response: Error</c> and <c>Message: Permission denied</c>: an AMI user without <c>reporting</c>.</summary>
    PermissionDenied,
}

/// <summary>A caller the peer lists waiting in <see cref="BootingAsterisk.QueueName"/> on <c>QueueStatus</c>.</summary>
/// <param name="Wait">The <c>Wait</c> header: seconds the caller had waited when Asterisk answered.</param>
internal sealed record QueueCaller(string Channel, string UniqueId, int Position, long Wait = 0);

/// <summary>
/// Plays, over one <see cref="PipedSocket"/> session, an Asterisk that accepted the AMI login before its modules
/// had finished loading.
/// </summary>
/// <remarks>
/// <para>
/// The window it plays was measured on Asterisk 20.20.1, 22.9.0 and 23.4.1, over raw AMI sessions logged in right
/// after a container restart. Asterisk accepts the login 80–131 ms before <c>QueueStatus</c> works and 50–111 ms
/// before <c>Agents</c> does. Until then it answers each with <c>Response: Error</c> and a <c>Message</c> beginning
/// <c>Invalid/unknown command</c>, which is exactly what it answers for good when app_queue or app_agent_pool is not
/// loaded at all. It sends <c>FullyBooted</c> 17–28 ms after <c>QueueStatus</c> starts working, and only to an AMI
/// user whose read permissions include <c>system</c>. An Asterisk that has already started sends it right after the
/// login's response.
/// </para>
/// <para>
/// One clock: the peer boots only when the test calls <see cref="BootAsync"/>, never on a timer. That call marks the
/// peer booted <b>before</b> it writes <c>FullyBooted</c>, so every action answered after the report is answered as
/// booted, and both come from the one trigger. <see cref="FirstRefusalAnswered"/> tells the test when to call it.
/// </para>
/// <para>
/// It answers <c>Status</c> with <see cref="StatusChannels"/>, or refuses it with <c>Permission denied</c> from the
/// <see cref="StatusRefusedFromAsk"/>-th ask on; <c>QueueStatus</c> with one queue, <c>rqq</c> (<c>ringall</c>, one
/// static member, and the callers of <see cref="QueueEntries"/>); <c>Agents</c> with agent <c>1001</c>; <c>BridgeList</c> with
/// <see cref="Bridges"/>; <c>ConfbridgeListRooms</c> and <c>ConfbridgeList</c> with <see cref="Rooms"/>, as
/// <see cref="ConferenceListing"/> says; an async <c>Originate</c>, when
/// <see cref="OriginateOutcome"/> is set, with its <c>Response: Success</c> and then one <c>OriginateResponse</c>, as
/// Asterisk 20.20.1, 22.9.0 and 23.4.1 do; and anything else with <c>Success</c>. It counts every action it reads, by
/// name, and <see cref="AskedAtLeast"/> turns a count into a signal.
/// </para>
/// </remarks>
internal sealed class BootingAsterisk
{
    /// <summary>The queue the peer reports.</summary>
    public const string QueueName = "rqq";

    /// <summary>The strategy of <see cref="QueueName"/>.</summary>
    public const string QueueStrategy = "ringall";

    /// <summary>The location of the queue's one static member.</summary>
    public const string MemberLocation = "Local/agent@rq/n";

    /// <summary>The agent the peer reports.</summary>
    public const string AgentId = "1001";

    /// <summary>A refusal that is not an unknown command, which Asterisk also sends.</summary>
    public const string PermissionDenied = "Permission denied";

    /// <summary>The channel an accepted originate reports in its <c>OriginateResponse</c>.</summary>
    public const string OriginatedChannel = "Local/s@hold-00000132;1";

    private readonly ConcurrentDictionary<string, int> _asks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _askWaitersGate = new();
    private readonly List<(string Action, int Times, TaskCompletionSource Signal)> _askWaiters = [];
    private readonly TaskCompletionSource _firstRefusalAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _served = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PipedSocket? _socket;
    private bool _booted;
    private int _refusals;

    /// <summary>Whether the peer has finished starting when the session logs in.</summary>
    public bool BootedAtLogin { get; init; }

    /// <summary>
    /// Whether the peer sends <c>FullyBooted</c>: <see langword="true"/> for an AMI user with <c>system</c> in its read
    /// permissions, <see langword="false"/> for one without it.
    /// </summary>
    public bool SendsFullyBooted { get; init; } = true;

    /// <summary>Where a peer booted at the login sends <c>FullyBooted</c> during the connect.</summary>
    public FullyBootedPlacement FullyBootedAt { get; init; } = FullyBootedPlacement.AfterConnect;

    /// <summary>Whether app_queue is loaded: without it <c>QueueStatus</c> is refused for good.</summary>
    public bool HasAppQueue { get; init; } = true;

    /// <summary>Whether app_agent_pool is loaded: without it <c>Agents</c> is refused for good.</summary>
    public bool HasAgentPool { get; init; } = true;

    /// <summary>
    /// The <c>Message</c> of every refusal, or <see langword="null"/> for Asterisk's
    /// <c>Invalid/unknown command: &lt;action&gt;. …</c>.
    /// </summary>
    public string? RefusalMessage { get; init; }

    /// <summary>When the peer ends the session on its own. Settable between two loads.</summary>
    public PeerClose Close { get; set; } = PeerClose.Never;

    /// <summary>The action that ends the session under <see cref="PeerClose.WhenAsked"/>.</summary>
    public string? CloseWhenAsked { get; set; }

    /// <summary>How many channels a <c>Status</c> list holds before the close under <see cref="PeerClose.DuringStatus"/>.</summary>
    public int StatusChannelsBeforeClose { get; set; }

    /// <summary>
    /// From which <c>Status</c> ask on this session, counting from 1, the peer refuses it with <c>Response: Error</c> and
    /// <c>Message: Permission denied</c>, as Asterisk does for an AMI user whose write classes allow none of
    /// <c>system</c>, <c>call</c> or <c>reporting</c>; 0 never refuses it. Settable between two loads.
    /// </summary>
    public int StatusRefusedFromAsk { get; set; }

    /// <summary>
    /// The <c>Response</c> of the <c>OriginateResponse</c> the peer sends after accepting an <c>Originate</c>:
    /// <c>Success</c> or <c>Failure</c>; <see langword="null"/> answers an <c>Originate</c> like any other action.
    /// </summary>
    public string? OriginateOutcome { get; init; }

    /// <summary>
    /// How long the destination of an accepted <c>Originate</c> rings before Asterisk reports its outcome: the peer
    /// writes the <c>Response: Success</c> at once and the <c>OriginateResponse</c> this long after it, as Asterisk does
    /// for a destination that answers late (8 s after the action for a dialplan <c>Wait(8)</c> before <c>Answer</c>,
    /// measured on 20.20.1, 22.9.0 and 23.4.1). <see cref="TimeSpan.Zero"/> reports it at once.
    /// </summary>
    public TimeSpan OriginateRings { get; init; }

    /// <summary>
    /// The callers the peer lists waiting in <see cref="QueueName"/> on <c>QueueStatus</c>, one <c>QueueEntry</c> each,
    /// after the queue's member, with the headers Asterisk 22.9.0 sent for one (captured in the queue-reload recording).
    /// Settable between two loads.
    /// </summary>
    public IReadOnlyList<QueueCaller> QueueEntries { get; set; } = [];

    /// <summary>The channels the peer lists on <c>Status</c>. Settable between two loads.</summary>
    public IReadOnlyList<StatusChannel> StatusChannels { get; set; } = [];

    /// <summary>
    /// The bridges the peer lists on <c>BridgeList</c>, in the frames Asterisk 22.12, 23.6 and 24.0 rc2 sent (the 0.1
    /// captures): <c>Response: Success</c> with <c>EventList: start</c>, one <c>BridgeListItem</c> each, and
    /// <c>BridgeListComplete</c>. <c>BridgeList</c> needs no class, so it is never refused. Settable between two loads.
    /// </summary>
    public IReadOnlyList<ListedBridge> Bridges { get; set; } = [];

    /// <summary>
    /// The ConfBridge rooms the peer lists on <c>ConfbridgeListRooms</c>, and on <c>ConfbridgeList</c> for the room the
    /// action names, in the frames of the 0.1 captures. A <c>ConfbridgeList</c> for a room not listed is answered
    /// <c>No Conference by that name found.</c>, as Asterisk does. Settable between two loads.
    /// </summary>
    public IReadOnlyList<ListedRoom> Rooms { get; set; } = [];

    /// <summary>How the peer answers both conference listings. Settable between two loads.</summary>
    public ConferenceListing ConferenceListing { get; set; } = ConferenceListing.Listed;

    /// <summary>
    /// When <see langword="true"/>, the peer answers <c>QueueStatus</c> with its <c>Response</c>, the queue's
    /// <c>QueueParams</c> and its <c>QueueMember</c>, and then writes nothing more for it: the list stays open, so the
    /// session can end in the middle of the load's read. Settable between two loads.
    /// </summary>
    public bool QueueStatusStopsAfterMember { get; set; }

    /// <summary>Whether the listing includes its queue params; false permits a genuinely empty retry answer.</summary>
    public bool QueueParamsIncluded { get; set; } = true;

    /// <summary>Runs after the queue rows are written and before their completion, to order unsolicited events on the wire.</summary>
    public Func<Task>? BeforeQueueStatusComplete { get; set; }

    /// <summary>A final refusal after partial queue rows, instead of a successful completion.</summary>
    public string? QueueStatusRefusalAfterRows { get; set; }

    /// <summary>The member values to report on the next queue answer, independently of an older answer already written.</summary>
    public bool QueueMemberIncluded { get; set; } = true;
    public bool QueueMemberPaused { get; set; }
    public int QueueMemberStatus { get; set; } = 1;

    /// <summary>
    /// When set, the peer answers <c>Status</c> — with its list, its refusal or its close — only once this task has
    /// completed, so a test can act while the connection's read of it is open. The peer counts the ask before it waits,
    /// so <see cref="AskedAtLeast"/> says when the read is open. A wait that outlives <see cref="Run.Bound"/> fails the
    /// peer. Settable between two loads.
    /// </summary>
    public Task? StatusAnsweredAfter { get; set; }

    /// <summary>
    /// Actions, by name, that the peer answers only once the task given for them has completed, so a test can act while
    /// the connection waits for that answer: the peer counts the ask before it waits, so <see cref="AskedAtLeast"/> says
    /// when the wait has begun. A wait that outlives <see cref="Run.Bound"/> fails the peer. Settable between two loads.
    /// </summary>
    public IReadOnlyDictionary<string, Task> AnsweredAfter { get; set; } = new Dictionary<string, Task>();

    /// <summary>Whether the peer answers as booted.</summary>
    public bool IsBooted => Volatile.Read(ref _booted);

    /// <summary>Completes once the peer has answered its first refusal, and closed the session when it closes on it.</summary>
    public Task FirstRefusalAnswered => _firstRefusalAnswered.Task;

    /// <summary>Completes once the peer has stopped serving: the session ended, or the run did.</summary>
    public Task Served => _served.Task;

    /// <summary>What went wrong inside the peer, if anything did. A healthy run leaves it <see langword="null"/>.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>How many times the connection sent the action named <paramref name="action"/> on this session.</summary>
    public int Asked(string action) => _asks.TryGetValue(action, out var count) ? count : 0;

    /// <summary>
    /// Completes once the connection has sent the action named <paramref name="action"/> at least
    /// <paramref name="times"/> times on this session, counting the ones already sent. The peer counts an action when
    /// it reads it, before it answers.
    /// </summary>
    public Task AskedAtLeast(string action, int times)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        lock (_askWaitersGate)
        {
            if (Asked(action) >= times)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _askWaiters.Add((action, times, signal));
            return signal.Task;
        }
    }

    /// <summary>
    /// Asterisk finishes starting: the peer answers as booted from now on, then reports <c>FullyBooted</c> when
    /// <see cref="SendsFullyBooted"/>. Returns <see langword="false"/> when the report could not be written because
    /// the session had already ended.
    /// </summary>
    public async Task<bool> BootAsync()
    {
        var socket = Volatile.Read(ref _socket)
            ?? throw new InvalidOperationException("The peer boots the session it serves, and it serves none yet.");

        // One clock: booted first, then the report, so no action is refused after Asterisk has said it has started.
        Volatile.Write(ref _booted, true);
        return !SendsFullyBooted || await WriteFullyBootedAsync(socket);
    }

    /// <summary>Ends the session from the peer's side, as Asterisk does when it closes it.</summary>
    public void CloseSession() =>
        (Volatile.Read(ref _socket)
            ?? throw new InvalidOperationException("The peer closes the session it serves, and it serves none yet."))
        .CloseFromPeer();

    /// <summary>
    /// Asterisk reports an event nobody asked for, such as a channel's <c>Newchannel</c> or <c>Hangup</c>, on the
    /// session this peer serves. Returns <see langword="false"/> when the session had already ended.
    /// </summary>
    public Task<bool> SendEventAsync(string eventType, IEnumerable<KeyValuePair<string, string>> fields) =>
        (Volatile.Read(ref _socket)
            ?? throw new InvalidOperationException("The peer reports events on the session it serves, and it serves none yet."))
        .WriteEventAsync(eventType, fields);

    /// <summary>
    /// The actions the connection sent on this session after it ended, by name, once the peer has stopped serving.
    /// The peer reads nothing after a close, so these are the actions it was sent and never answered.
    /// </summary>
    public async Task<IReadOnlyList<string>> ActionsSentAfterCloseAsync(TimeSpan bound)
    {
        await Served.WaitAsync(bound);
        var socket = Volatile.Read(ref _socket);
        return socket is null ? [] : [.. socket.TakeUnreadActions().Select(ActionName)];
    }

    /// <summary>
    /// Serves one session until it ends. It never throws: a failure is kept in <see cref="Fault"/>, and the run's
    /// cancellation ends it quietly.
    /// </summary>
    public async Task ServeAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.CompareExchange(ref _socket, socket, null) is not null)
                throw new InvalidOperationException("A BootingAsterisk serves one session.");

            Volatile.Write(ref _booted, BootedAtLogin);
            if (!await AnswerConnectAsync(socket, cancellationToken))
                return;

            if (Close == PeerClose.AfterConnect)
            {
                socket.CloseFromPeer();
                return;
            }

            while (await socket.ReadActionAsync(cancellationToken) is { } action)
            {
                if (!await AnswerAsync(socket, action))
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended while the peer was serving. Nothing went wrong with the session itself.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Kept for the test to assert on: a peer that failed would otherwise read as an Asterisk that went quiet.
            // Running out of memory is not the peer's failure, so it is left to end the run.
            Fault = ex;
        }
        finally
        {
            _served.TrySetResult();
        }
    }

    /// <summary>Plays the banner, the MD5 challenge, the login and the version probe; false once the session ended.</summary>
    private async Task<bool> AnswerConnectAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        var reportsAtLogin = BootedAtLogin && SendsFullyBooted;
        if (!await socket.WriteAsync("Asterisk Call Manager/6.0.0\r\n"))
            return false;

        if (await ReadCountedAsync(socket, cancellationToken) is not { } challenge)
            return false;

        await socket.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "840415273")]);

        if (await ReadCountedAsync(socket, cancellationToken) is not { } login)
            return false;

        if (reportsAtLogin && FullyBootedAt == FullyBootedPlacement.BeforeLoginResponse)
            await WriteFullyBootedAsync(socket);

        await socket.RespondAsync("Success", PipedSocket.ActionIdOf(login), [new("Message", "Authentication accepted")]);

        if (reportsAtLogin && FullyBootedAt == FullyBootedPlacement.AfterLoginResponse)
            await WriteFullyBootedAsync(socket);

        if (await ReadCountedAsync(socket, cancellationToken) is not { } versionProbe)
            return false;

        await socket.RespondAsync("Success", PipedSocket.ActionIdOf(versionProbe),
            [new("AMIversion", "9.0.0"), new("AsteriskVersion", "22.9.0")]);

        if (reportsAtLogin && FullyBootedAt == FullyBootedPlacement.AfterConnect)
            await WriteFullyBootedAsync(socket);

        return true;
    }

    /// <summary>Answers one action after the connect; false once the peer has ended the session.</summary>
    private async Task<bool> AnswerAsync(PipedSocket socket, string action)
    {
        var name = ActionName(action);
        Count(name);
        var id = PipedSocket.ActionIdOf(action);

        if (Close == PeerClose.WhenAsked && string.Equals(name, CloseWhenAsked, StringComparison.OrdinalIgnoreCase))
        {
            socket.CloseFromPeer();
            return false;
        }

        if (AnsweredAfter.FirstOrDefault(gate => string.Equals(gate.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value is { } answeredAfter)
        {
            await answeredAfter.WaitAsync(Run.Bound);
        }

        if (string.Equals(name, "Status", StringComparison.OrdinalIgnoreCase))
        {
            if (StatusAnsweredAfter is { } answerAfter)
                await answerAfter.WaitAsync(Run.Bound);

            if (StatusRefusedFromAsk > 0 && Asked(name) >= StatusRefusedFromAsk)
            {
                await socket.RespondAsync("Error", id, [new("Message", PermissionDenied)]);
                return true;
            }

            return await AnswerStatusAsync(socket, id);
        }

        if (OriginateOutcome is { } outcome && string.Equals(name, "Originate", StringComparison.OrdinalIgnoreCase))
        {
            await AnswerOriginateAsync(socket, id, outcome, OriginateRings);
            return true;
        }

        if (string.Equals(name, "QueueStatus", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsBooted || !HasAppQueue)
                return await RefuseAsync(socket, id, name);

            await AnswerQueueStatusAsync(socket, id);
            return true;
        }

        if (string.Equals(name, "Agents", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsBooted || !HasAgentPool)
                return await RefuseAsync(socket, id, name);

            await AnswerAgentsAsync(socket, id);
            return true;
        }

        if (string.Equals(name, "BridgeList", StringComparison.OrdinalIgnoreCase))
        {
            await AnswerBridgeListAsync(socket, id, Bridges);
            return true;
        }

        if (string.Equals(name, "ConfbridgeListRooms", StringComparison.OrdinalIgnoreCase))
        {
            await AnswerConfbridgeListRoomsAsync(socket, id);
            return true;
        }

        if (string.Equals(name, "ConfbridgeList", StringComparison.OrdinalIgnoreCase))
        {
            await AnswerConfbridgeListAsync(socket, id, HeaderOf(action, "Conference"));
            return true;
        }

        await socket.RespondAsync("Success", id);
        return true;
    }

    private static async Task AnswerBridgeListAsync(PipedSocket socket, string id, IReadOnlyList<ListedBridge> bridges)
    {
        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Bridge listing will follow")]);
        foreach (var bridge in bridges)
        {
            await socket.WriteEventAsync("BridgeListItem",
            [
                new("ActionID", id), new("BridgeUniqueid", bridge.Id), new("BridgeType", bridge.Type),
                new("BridgeTechnology", bridge.Technology), new("BridgeCreator", bridge.Creator),
                new("BridgeName", bridge.Name),
                new("BridgeNumChannels", bridge.NumChannels.ToString(CultureInfo.InvariantCulture)),
                new("BridgeVideoSourceMode", "none"),
            ]);
        }

        await socket.WriteEventAsync("BridgeListComplete",
        [
            new("ActionID", id), new("EventList", "Complete"),
            new("ListItems", bridges.Count.ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    private async Task AnswerConfbridgeListRoomsAsync(PipedSocket socket, string id)
    {
        var rooms = Rooms;
        if (await RefusedOrEmptyAsync(socket, id, rooms.Count == 0))
            return;

        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Confbridge conferences will follow")]);
        foreach (var room in rooms)
        {
            await socket.WriteEventAsync("ConfbridgeListRooms",
            [
                new("ActionID", id), new("Conference", room.Conference),
                new("Parties", room.Participants.Count.ToString(CultureInfo.InvariantCulture)), new("Marked", "0"),
                new("Locked", "No"), new("Muted", "No"),
            ]);
        }

        await socket.WriteEventAsync("ConfbridgeListRoomsComplete",
        [
            new("ActionID", id), new("EventList", "Complete"),
            new("ListItems", rooms.Count.ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    private async Task AnswerConfbridgeListAsync(PipedSocket socket, string id, string? conference)
    {
        var rooms = Rooms;
        if (await RefusedOrEmptyAsync(socket, id, rooms.Count == 0))
            return;

        var room = rooms.FirstOrDefault(r => string.Equals(r.Conference, conference, StringComparison.Ordinal));
        if (room is null)
        {
            await socket.RespondAsync("Error", id, [new("Message", "No Conference by that name found.")]);
            return;
        }

        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Confbridge user list will follow")]);
        foreach (var participant in room.Participants)
        {
            await socket.WriteEventAsync("ConfbridgeList",
            [
                new("ActionID", id), new("Conference", room.Conference), new("Admin", "No"), new("MarkedUser", "No"),
                new("WaitMarked", "No"), new("EndMarked", "No"), new("EndMarkedAny", "No"), new("Waiting", "No"),
                new("Muted", participant.Muted ? "Yes" : "No"), new("Talking", participant.Talking ? "Yes" : "No"),
                new("AnsweredTime", "1"), new("Channel", participant.Channel), new("ChannelState", "6"),
                new("ChannelStateDesc", "Up"), new("CallerIDNum", "7002"), new("CallerIDName", "Capture"),
                new("ConnectedLineNum", "7002"), new("ConnectedLineName", "Capture"), new("Language", "en"),
                new("AccountCode", ""), new("Context", "capture"), new("Exten", "conf"), new("Priority", "3"),
                new("Uniqueid", participant.UniqueId), new("Linkedid", participant.LinkedId),
            ]);
        }

        await socket.WriteEventAsync("ConfbridgeListComplete",
        [
            new("ActionID", id), new("EventList", "Complete"),
            new("ListItems", room.Participants.Count.ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    /// <summary>
    /// Answers a conference listing with Asterisk's refusal, or with its error for an empty listing; true when it did.
    /// </summary>
    private async Task<bool> RefusedOrEmptyAsync(PipedSocket socket, string id, bool empty)
    {
        if (ConferenceListing == ConferenceListing.PermissionDenied)
        {
            await socket.RespondAsync("Error", id, [new("Message", PermissionDenied)]);
            return true;
        }

        if (!empty)
            return false;

        await socket.RespondAsync("Error", id, [new("Message", "No active conferences.")]);
        return true;
    }

    /// <summary>The value of the header <paramref name="header"/> in an action's raw text, or null when it has none.</summary>
    private static string? HeaderOf(string action, string header)
    {
        foreach (var line in action.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon].Trim(), header, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private async Task<bool> RefuseAsync(PipedSocket socket, string id, string name)
    {
        var message = RefusalMessage
            ?? $"Invalid/unknown command: {name}. Use Action: ListCommands to show available commands.";
        await socket.RespondAsync("Error", id, [new("Message", message)]);

        var first = Interlocked.Increment(ref _refusals) == 1;
        var closes = first && Close == PeerClose.AfterFirstRefusal;
        if (closes)
            socket.CloseFromPeer();

        if (first)
            _firstRefusalAnswered.TrySetResult();

        return !closes;
    }

    private async Task<bool> AnswerStatusAsync(PipedSocket socket, string id)
    {
        var channels = StatusChannels;
        int? closeAfter = Close == PeerClose.DuringStatus ? StatusChannelsBeforeClose : null;
        if (closeAfter is { } bound && (bound < 0 || bound > channels.Count))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"A Status list of {channels.Count} channel(s) cannot close after {bound}."));

        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Channel status will follow")]);
        for (var i = 0; i < channels.Count; i++)
        {
            if (closeAfter == i)
            {
                socket.CloseFromPeer();
                return false;
            }

            await socket.WriteEventAsync("Status", StatusFields(channels[i], id));
        }

        if (closeAfter == channels.Count)
        {
            socket.CloseFromPeer();
            return false;
        }

        await socket.WriteEventAsync("StatusComplete",
        [
            new("ActionID", id), new("EventList", "Complete"),
            new("ListItems", channels.Count.ToString(CultureInfo.InvariantCulture)),
        ]);
        return true;
    }

    /// <summary>The order and headers Asterisk 22.9.0 wrote for an async originate, raw.</summary>
    private static async Task AnswerOriginateAsync(PipedSocket socket, string id, string outcome, TimeSpan rings)
    {
        await socket.RespondAsync("Success", id, [new("Message", "Originate successfully queued")]);
        if (rings > TimeSpan.Zero)
        {
            // fence-allow: SIMULATED-WORK — the destination rings this long before Asterisk reports the outcome
            await Task.Delay(rings);
        }

        var success = string.Equals(outcome, "Success", StringComparison.Ordinal);
        await socket.WriteEventAsync("OriginateResponse",
        [
            new("Privilege", "call,all"), new("ActionID", id), new("Response", outcome),
            new("Channel", success ? OriginatedChannel : "Nosuchtech/x"), new("Context", "hold"), new("Exten", "s"),
            new("Reason", success ? "4" : "0"), new("Uniqueid", success ? "1790763823.612" : "<unknown>"),
            new("CallerIDNum", "<unknown>"), new("CallerIDName", "<unknown>"),
        ]);
    }

    private async Task AnswerQueueStatusAsync(PipedSocket socket, string id)
    {
        var entries = QueueEntries;
        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Queue status will follow")]);
        if (QueueParamsIncluded)
            await socket.WriteEventAsync("QueueParams",
        [
            new("ActionID", id), new("Queue", QueueName), new("Max", "0"), new("Strategy", QueueStrategy),
            new("Calls", "0"), new("Holdtime", "0"), new("TalkTime", "0"), new("Completed", "0"),
            new("Abandoned", "0"),
        ]);
        var memberCount = QueueMemberIncluded ? 1 : 0;
        if (QueueMemberIncluded)
        {
            await socket.WriteEventAsync("QueueMember",
            [
                new("ActionID", id), new("Queue", QueueName), new("Name", "Agent One"), new("Location", MemberLocation),
                new("StateInterface", "Custom:ag1"), new("Penalty", "0"),
                new("Status", QueueMemberStatus.ToString(CultureInfo.InvariantCulture)),
                new("Paused", QueueMemberPaused ? "1" : "0"),
            ]);
        }
        if (QueueStatusStopsAfterMember)
            return;

        foreach (var caller in entries)
        {
            await socket.WriteEventAsync("QueueEntry",
            [
                new("Queue", QueueName), new("Position", caller.Position.ToString(CultureInfo.InvariantCulture)),
                new("Channel", caller.Channel), new("Uniqueid", caller.UniqueId), new("CallerIDNum", "5552101"),
                new("CallerIDName", "QRA"), new("ConnectedLineNum", "unknown"), new("ConnectedLineName", "unknown"),
                new("Wait", caller.Wait.ToString(CultureInfo.InvariantCulture)), new("Priority", "0"),
                new("ActionID", id),
            ]);
        }

        if (BeforeQueueStatusComplete is { } beforeComplete)
            await beforeComplete();

        if (QueueStatusRefusalAfterRows is { } refusal)
        {
            await socket.RespondAsync("Error", id, [new("Message", refusal)]);
            return;
        }

        await socket.WriteEventAsync("QueueStatusComplete",
        [
            new("ActionID", id), new("EventList", "Complete"),
            new("ListItems", ((QueueParamsIncluded ? 1 : 0) + memberCount + entries.Count).ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    private static async Task AnswerAgentsAsync(PipedSocket socket, string id)
    {
        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Agents will follow")]);
        await socket.WriteEventAsync("Agents",
            [new("ActionID", id), new("Agent", AgentId), new("Name", "Agent 1001"), new("Status", "AGENT_LOGGEDOFF")]);
        await socket.WriteEventAsync("AgentsComplete",
            [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "1")]);
    }

    private static Task<bool> WriteFullyBootedAsync(PipedSocket socket) =>
        socket.WriteEventAsync("FullyBooted",
            [new("Privilege", "system,all"), new("Uptime", "0"), new("LastReload", "0"), new("Status", "Fully Booted")]);

    /// <summary>Reads the next action and counts it; null once the session ended.</summary>
    private async Task<string?> ReadCountedAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        var action = await socket.ReadActionAsync(cancellationToken);
        if (action is not null)
            Count(ActionName(action));

        return action;
    }

    /// <summary>Counts one ask of <paramref name="name"/> and completes every <see cref="AskedAtLeast"/> it reaches.</summary>
    private void Count(string name)
    {
        var count = _asks.AddOrUpdate(name, 1, static (_, count) => count + 1);
        List<TaskCompletionSource>? reached = null;
        lock (_askWaitersGate)
        {
            for (var i = _askWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _askWaiters[i];
                if (string.Equals(waiter.Action, name, StringComparison.OrdinalIgnoreCase) && count >= waiter.Times)
                {
                    (reached ??= []).Add(waiter.Signal);
                    _askWaiters.RemoveAt(i);
                }
            }
        }

        foreach (var signal in reached ?? [])
            signal.TrySetResult();
    }

    private static KeyValuePair<string, string>[] StatusFields(StatusChannel channel, string id) =>
    [
        new("ActionID", id),
        new("Channel", channel.Channel),
        new("ChannelState", channel.ChannelState.ToString(CultureInfo.InvariantCulture)),
        new("ChannelStateDesc", ChannelStateDesc(channel.ChannelState)),
        new("CallerIDNum", "1001"),
        new("CallerIDName", "Agent 1001"),
        new("Context", "from-internal"),
        new("Exten", "600"),
        new("Priority", "1"),
        new("Uniqueid", channel.UniqueId),
        new("Linkedid", channel.LinkedId),
        .. BridgeIdField(channel),
    ];

    private static KeyValuePair<string, string>[] BridgeIdField(StatusChannel channel) =>
        channel.BridgeId is { } bridgeId ? [new("BridgeID", bridgeId)] : [];

    /// <summary>The text Asterisk writes beside each numeric channel state.</summary>
    private static string ChannelStateDesc(int state) => state switch
    {
        0 => "Down",
        1 => "Rsrvd",
        2 => "OffHook",
        3 => "Dialing",
        4 => "Ring",
        5 => "Ringing",
        6 => "Up",
        7 => "Busy",
        8 => "Dialing Offhook",
        9 => "Pre-ring",
        _ => "Unknown",
    };

    /// <summary>The name on an action's <c>Action:</c> line, which the connection writes first.</summary>
    private static string ActionName(string action)
    {
        const string prefix = "Action:";
        var end = action.IndexOf("\r\n", StringComparison.Ordinal);
        var first = end >= 0 ? action[..end] : action;
        return first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? first[prefix.Length..].Trim() : first.Trim();
    }
}

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
internal sealed record StatusChannel(string UniqueId, string Channel, string LinkedId, int ChannelState = 6);

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
/// It answers <c>Status</c> with <see cref="StatusChannels"/>; <c>QueueStatus</c> with one queue, <c>rqq</c>
/// (<c>ringall</c>, one static member); <c>Agents</c> with agent <c>1001</c>; and anything else with
/// <c>Success</c>. It counts every action it reads, by name.
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

    private readonly ConcurrentDictionary<string, int> _asks = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The channels the peer lists on <c>Status</c>. Settable between two loads.</summary>
    public IReadOnlyList<StatusChannel> StatusChannels { get; set; } = [];

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
        catch (Exception ex)
        {
            // Kept for the test to assert on: a peer that failed would otherwise read as an Asterisk that went quiet.
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
        _asks.AddOrUpdate(name, 1, static (_, count) => count + 1);
        var id = PipedSocket.ActionIdOf(action);

        if (Close == PeerClose.WhenAsked && string.Equals(name, CloseWhenAsked, StringComparison.OrdinalIgnoreCase))
        {
            socket.CloseFromPeer();
            return false;
        }

        if (string.Equals(name, "Status", StringComparison.OrdinalIgnoreCase))
            return await AnswerStatusAsync(socket, id);

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

        await socket.RespondAsync("Success", id);
        return true;
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

    private static async Task AnswerQueueStatusAsync(PipedSocket socket, string id)
    {
        await socket.RespondAsync("Success", id,
            [new("EventList", "start"), new("Message", "Queue status will follow")]);
        await socket.WriteEventAsync("QueueParams",
        [
            new("ActionID", id), new("Queue", QueueName), new("Max", "0"), new("Strategy", QueueStrategy),
            new("Calls", "0"), new("Holdtime", "0"), new("TalkTime", "0"), new("Completed", "0"),
            new("Abandoned", "0"),
        ]);
        await socket.WriteEventAsync("QueueMember",
        [
            new("ActionID", id), new("Queue", QueueName), new("Name", "Agent One"), new("Location", MemberLocation),
            new("StateInterface", "Custom:ag1"), new("Penalty", "0"), new("Status", "1"), new("Paused", "0"),
        ]);
        await socket.WriteEventAsync("QueueStatusComplete",
            [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "2")]);
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
            _asks.AddOrUpdate(ActionName(action), 1, static (_, count) => count + 1);

        return action;
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
    ];

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

namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager
{
    /// <summary>
    /// Copies every actual index entry for friend tests. The caller must first quiesce its scenario's producers and
    /// await its causal notification gates: these separate dictionary copies are not an atomic concurrent snapshot.
    /// Bridge values remain raw session ids, including any id with no held instance. No live dictionary is exposed.
    /// </summary>
    internal SessionIndexSnapshot InspectIndexes()
    {
        var indexes = _serverIndexes.Select(e => new ServerIndexSnapshot(e.Key, e.Value.ByLinkedId.ToArray(),
            e.Value.ByChannelId.ToArray(), e.Value.BridgeToSession.ToArray())).ToArray();
        return new(_sessions.ToArray(), indexes.SelectMany(e => e.LinkedIds).ToArray(),
            indexes.SelectMany(e => e.ChannelIds).ToArray(), indexes.SelectMany(e => e.Bridges).ToArray())
        { ServerIndexes = indexes };
    }
}

internal sealed record SessionIndexSnapshot(
    IReadOnlyList<KeyValuePair<string, CallSession>> Sessions,
    IReadOnlyList<KeyValuePair<string, CallSession>> LinkedIds,
    IReadOnlyList<KeyValuePair<string, CallSession>> ChannelIds,
    IReadOnlyList<KeyValuePair<string, string>> Bridges)
{
    public IReadOnlyList<ServerIndexSnapshot> ServerIndexes { get; init; } = [];
}

internal sealed record ServerIndexSnapshot(string ServerId,
    IReadOnlyList<KeyValuePair<string, CallSession>> LinkedIds,
    IReadOnlyList<KeyValuePair<string, CallSession>> ChannelIds,
    IReadOnlyList<KeyValuePair<string, string>> Bridges);

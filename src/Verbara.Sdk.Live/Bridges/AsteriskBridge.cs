using System.Collections.Concurrent;

namespace Verbara.Sdk.Live.Bridges;

/// <summary>
/// Represents an Asterisk bridge (conference/two-party call bridge) tracked in real time.
/// </summary>
public sealed class AsteriskBridge : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();

    public override string Id => BridgeUniqueid;

    /// <summary>Unique identifier assigned by Asterisk.</summary>
    public string BridgeUniqueid { get; init; } = string.Empty;

    /// <summary>Bridge type (e.g. "basic", "multimix").</summary>
    public string? BridgeType { get; set; }

    /// <summary>Bridge technology (e.g. "simple_bridge", "softmix").</summary>
    public string? Technology { get; set; }

    /// <summary>Name of the application that created the bridge.</summary>
    public string? Creator { get; set; }

    /// <summary>Optional human-readable name for the bridge.</summary>
    public string? Name { get; set; }

    /// <summary>Set of channel unique IDs currently in this bridge.</summary>
    public ConcurrentDictionary<string, byte> Channels { get; } = new();

    /// <summary>Number of channels currently in this bridge.</summary>
    public int NumChannels => Channels.Count;

    /// <summary>When the bridge was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the bridge was destroyed, or <c>null</c> if still active.</summary>
    public DateTimeOffset? DestroyedAt { get; set; }

    // When the manager inserted the bridge, on its change ordinal: a snapshot requested before it cannot list it.
    internal long InsertMark { get; set; }

    // 1 while the bridge is held only because a listing admitted it, silently: Asterisk's BridgeCreate for it, delivered
    // later, is then its observed creation and is announced once. 0 once announced, or for a bridge created live.
    private int _admittedBySnapshot;

    internal bool AdmittedBySnapshot
    {
        get => Volatile.Read(ref _admittedBySnapshot) != 0;
        init => _admittedBySnapshot = value ? 1 : 0;
    }

    // Claims the announcement of a snapshot-admitted bridge's creation: true once, for a bridge not destroyed since.
    internal bool TryClaimObservedCreate()
    {
        lock (SyncRoot)
            return DestroyedAt is null && Interlocked.Exchange(ref _admittedBySnapshot, 0) != 0;
    }

    // When each channel in Channels entered, on the manager's change ordinal: a snapshot requested before a channel
    // entered cannot list it in the bridge. Writers hold the manager's window gate then SyncRoot; the manager may
    // inspect another bridge's marks under that shared gate without acquiring a second bridge lock.
    internal Dictionary<string, long> EnterMarks { get; } = new(StringComparer.Ordinal);
}

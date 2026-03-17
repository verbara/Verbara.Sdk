using System.Collections.Concurrent;

namespace Asterisk.Sdk.Live.Bridges;

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
}

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Verbara.Sdk.Live.Conferences;

/// <summary>A ConfBridge (or app_meetme, fed by direct calls) conference room that <see cref="ConferenceManager"/> holds.</summary>
/// <remarks>Only the manager creates rooms.</remarks>
public class ConferenceRoom
{
    internal readonly Lock SyncRoot = new();

    internal ConferenceRoom()
    {
    }

    // Set, under the manager gate and SyncRoot, when the room leaves the table. Reentrant snapshot continuations must
    // not change that detached lifetime after an event subscriber creates its replacement.
    internal bool RemovedFromTable { get; set; }

    /// <summary>The conference name Asterisk sent.</summary>
    public string RoomNumber { get; internal init; } = string.Empty;

    // The participants, keyed by channel name. Users is a read-only view of it; only the manager writes it.
    internal ConcurrentDictionary<string, ConferenceUser> Table { get; } = new();

    /// <summary>
    /// The participants still held in the room, keyed by the channel name Asterisk sent on each participant's join. One
    /// entry per channel. A read-only view of the room's own table: it follows the table as the manager changes it, and
    /// each read of this property returns a new view.
    /// </summary>
    public IReadOnlyDictionary<string, ConferenceUser> Users => new ConferenceUserView(Table);

    /// <summary>The number of participants still held in the room.</summary>
    public int UserCount => Table.Count;
}

/// <summary>A read-only view of a room's participant table under the conference names.</summary>
internal sealed class ConferenceUserView(ConcurrentDictionary<string, ConferenceUser> table) : IReadOnlyDictionary<string, ConferenceUser>
{
    public ConferenceUser this[string key] => table[key];

    public IEnumerable<string> Keys => table.Keys;

    public IEnumerable<ConferenceUser> Values => table.Values;

    public int Count => table.Count;

    public bool ContainsKey(string key) => table.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out ConferenceUser value)
    {
        if (table.TryGetValue(key, out var user))
        {
            value = user;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, ConferenceUser>> GetEnumerator()
    {
        foreach (var (channel, user) in table)
            yield return new KeyValuePair<string, ConferenceUser>(channel, user);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Text;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Static pool of interned strings for AMI protocol keys and common values.
/// Eliminates repeated string allocations for the ~70 known field keys and ~35 common values.
/// Lookup is O(1) by length, then O(n) linear scan within the length bucket (typically 1-5 entries).
/// </summary>
internal static class AmiStringPool
{
    private static readonly (byte[] Utf8, string Str)[]?[] s_keys = new (byte[], string)[]?[32];
    private static readonly (byte[] Utf8, string Str)[]?[] s_values = new (byte[], string)[]?[24];

    static AmiStringPool()
    {
        BuildPool(s_keys,
        [
            // ManagerEvent base
            "Event", "Privilege", "Uniqueid", "Timestamp",
            // Response
            "Response", "ActionID", "Message",
            // ChannelEventBase (inherited by ~70% of events)
            "Channel", "ChannelState", "ChannelStateDesc",
            "CallerIDNum", "CallerIDName",
            "ConnectedLineNum", "ConnectedLineName",
            "AccountCode", "Context", "Exten", "Priority", "Language", "Linkedid",
            // BridgeEventBase
            "BridgeUniqueid", "BridgeType", "BridgeTechnology", "BridgeCreator",
            "BridgeName", "BridgeNumChannels", "BridgeVideoSourceMode",
            // QueueMemberEventBase
            "Queue", "MemberName", "Interface", "StateInterface", "Membership",
            "Penalty", "CallsTaken", "Status", "Paused", "PausedReason",
            "Ringinuse", "LastCall", "LastPause", "InCall",
            // SecurityEventBase
            "EventTV", "Severity", "Service", "AccountID", "SessionID",
            "LocalAddress", "RemoteAddress", "SessionTV",
            // Common fields
            "Cause", "Cause-txt", "Variable", "Value", "Data",
            "Application", "AppData", "Duration", "Agent", "Conference",
            // Dial events (Dest* prefix)
            "DialStatus", "DestChannel", "DestChannelState", "DestChannelStateDesc",
            "DestCallerIDNum", "DestCallerIDName",
            "DestConnectedLineNum", "DestConnectedLineName",
            "DestAccountCode", "DestContext", "DestExten", "DestPriority",
            "DestLanguage", "DestLinkedid", "DestUniqueid",
        ]);

        BuildPool(s_values,
        [
            // Single digits (ChannelState, Priority, etc.)
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
            // Channel state descriptions
            "Down", "Rsrvd", "OffHook", "Dialing", "Ring", "Ringing", "Up", "Busy",
            // Response statuses
            "Success", "Error", "Follows", "Goodbye",
            // Boolean-like
            "true", "false", "yes", "no",
            // Common short values
            "en", "default",
            // Privilege values
            "call,all", "agent,all", "system,all", "command,all",
            "reporting,all", "security,all",
        ]);
    }

    /// <summary>
    /// Returns an interned string for known AMI keys, or allocates a new string for unknown keys.
    /// </summary>
    public static string GetKey(ReadOnlySpan<byte> utf8)
    {
        if ((uint)utf8.Length < (uint)s_keys.Length)
        {
            var group = s_keys[utf8.Length];
            if (group is not null)
            {
                foreach (var (poolUtf8, str) in group)
                {
                    if (utf8.SequenceEqual(poolUtf8))
                        return str;
                }
            }
        }

        return Encoding.UTF8.GetString(utf8);
    }

    /// <summary>
    /// Returns an interned string for common AMI values, or allocates a new string for uncommon values.
    /// </summary>
    public static string GetValue(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
            return string.Empty;

        if ((uint)utf8.Length < (uint)s_values.Length)
        {
            var group = s_values[utf8.Length];
            if (group is not null)
            {
                foreach (var (poolUtf8, str) in group)
                {
                    if (utf8.SequenceEqual(poolUtf8))
                        return str;
                }
            }
        }

        return Encoding.UTF8.GetString(utf8);
    }

    private static void BuildPool((byte[], string)[]?[] pool, string[] entries)
    {
        var groups = new Dictionary<int, List<(byte[], string)>>();
        foreach (var entry in entries)
        {
            if (entry.Length >= pool.Length) continue;
            if (!groups.TryGetValue(entry.Length, out var list))
            {
                list = [];
                groups[entry.Length] = list;
            }
            list.Add((Encoding.UTF8.GetBytes(entry), entry));
        }

        foreach (var (length, list) in groups)
        {
            pool[length] = [.. list];
        }
    }
}

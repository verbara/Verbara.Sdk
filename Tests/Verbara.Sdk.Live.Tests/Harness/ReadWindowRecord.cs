using System.Collections;
using System.Reflection;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// What a <see cref="ChannelManager"/> keeps for its read windows, read through reflection on its private fields: the
/// departures it has recorded for the reads in progress, and the windows still open. Both are private on purpose, and
/// both must return to zero once every read has ended, by whatever route it ended.
/// </summary>
internal static class ReadWindowRecord
{
    /// <summary>How many departures the manager is keeping for its open read windows.</summary>
    public static int Departures(ChannelManager channels) => CountOf(channels, "_departures");

    /// <summary>How many read windows the manager holds open.</summary>
    public static int OpenWindows(ChannelManager channels) => CountOf(channels, "_openReadMarks");

    private static int CountOf(ChannelManager channels, string fieldName)
    {
        var field = typeof(ChannelManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ChannelManager has no private field {fieldName}.");
        var collection = field.GetValue(channels) as ICollection
            ?? throw new InvalidOperationException($"ChannelManager.{fieldName} is not a collection.");
        return collection.Count;
    }
}

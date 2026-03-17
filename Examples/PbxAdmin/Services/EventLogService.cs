using System.Collections.Concurrent;
using Asterisk.Sdk;

namespace PbxAdmin.Services;

public sealed class EventLogService
{
    private readonly ConcurrentQueue<EventLogEntry> _entries = new();
    private const int MaxEntries = 200;

    public void Add(string serverId, ManagerEvent evt)
    {
        var entry = new EventLogEntry(
            DateTimeOffset.UtcNow,
            serverId,
            evt.GetType().Name.Replace("Event", ""),
            evt.UniqueId,
            evt.RawFields?.GetValueOrDefault("Channel"));

        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<EventLogEntry> GetRecent(int count = 50) =>
        _entries.Reverse().Take(count).ToList();
}

public sealed record EventLogEntry(
    DateTimeOffset Timestamp,
    string ServerId,
    string EventType,
    string? UniqueId,
    string? Channel);

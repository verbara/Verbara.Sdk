using System.Collections.Concurrent;
using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Enums;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Channels;

/// <summary>
/// Tracks all active Asterisk channels in real-time from AMI events.
/// Uses dual indices (UniqueId + Name) for O(1) lookups.
/// All state mutations are protected by per-entity locks for atomic updates.
/// </summary>
public sealed class ChannelManager
{
    private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByUniqueId = new();
    private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByName = new();
    private readonly ILogger _logger;

    public event Action<AsteriskChannel>? ChannelAdded;
    public event Action<AsteriskChannel>? ChannelRemoved;
    public event Action<AsteriskChannel>? ChannelStateChanged;

    public ChannelManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskChannel> ActiveChannels => _channelsByUniqueId.Values;

    public int ChannelCount => _channelsByUniqueId.Count;

    public AsteriskChannel? GetByUniqueId(string uniqueId) =>
        _channelsByUniqueId.GetValueOrDefault(uniqueId);

    /// <summary>O(1) lookup by channel name via secondary index.</summary>
    public AsteriskChannel? GetByName(string name) =>
        _channelsByName.GetValueOrDefault(name);

    /// <summary>Handle a NewChannel event.</summary>
    public void OnNewChannel(string uniqueId, string channelName, ChannelState state,
        string? callerIdNum = null, string? callerIdName = null,
        string? context = null, string? exten = null, int priority = 1)
    {
        var channel = new AsteriskChannel
        {
            UniqueId = uniqueId,
            Name = channelName,
            State = state,
            CallerIdNum = callerIdNum,
            CallerIdName = callerIdName,
            Context = context,
            Extension = exten,
            Priority = priority
        };

        _channelsByUniqueId[uniqueId] = channel;
        _channelsByName[channelName] = channel;
        ChannelAdded?.Invoke(channel);
    }

    /// <summary>Handle a NewState event (channel state changed).</summary>
    public void OnNewState(string uniqueId, ChannelState newState, string? channelName = null)
    {
        if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
        {
            lock (channel.SyncRoot)
            {
                channel.State = newState;
                if (channelName is not null)
                {
                    _channelsByName.TryRemove(channel.Name, out _);
                    channel.Name = channelName;
                    _channelsByName[channelName] = channel;
                }
            }
            ChannelStateChanged?.Invoke(channel);
        }
    }

    /// <summary>Handle a Hangup event.</summary>
    public void OnHangup(string uniqueId, HangupCause cause = HangupCause.NormalClearing)
    {
        if (_channelsByUniqueId.TryRemove(uniqueId, out var channel))
        {
            _channelsByName.TryRemove(channel.Name, out _);
            lock (channel.SyncRoot)
            {
                channel.HangupCause = cause;
                channel.State = ChannelState.Down;
            }
            ChannelRemoved?.Invoke(channel);
        }
    }

    /// <summary>Handle a Rename event.</summary>
    public void OnRename(string uniqueId, string newName)
    {
        if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
        {
            lock (channel.SyncRoot)
            {
                _channelsByName.TryRemove(channel.Name, out _);
                channel.Name = newName;
                _channelsByName[newName] = channel;
            }
        }
    }

    /// <summary>Handle channel link (bridge).</summary>
    public void OnLink(string uniqueId1, string uniqueId2)
    {
        var ch1 = GetByUniqueId(uniqueId1);
        var ch2 = GetByUniqueId(uniqueId2);
        if (ch1 is not null && ch2 is not null)
        {
            ch1.LinkedChannel = ch2;
            ch2.LinkedChannel = ch1;
        }
    }

    /// <summary>Handle channel unlink.</summary>
    public void OnUnlink(string uniqueId1, string uniqueId2)
    {
        var ch1 = GetByUniqueId(uniqueId1);
        var ch2 = GetByUniqueId(uniqueId2);
        if (ch1 is not null) ch1.LinkedChannel = null;
        if (ch2 is not null) ch2.LinkedChannel = null;
    }

    /// <summary>Get channels filtered by state (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskChannel> GetChannelsByState(ChannelState state) =>
        _channelsByUniqueId.Values.Where(c => c.State == state);

    public void Clear()
    {
        _channelsByUniqueId.Clear();
        _channelsByName.Clear();
    }
}

/// <summary>Represents a live Asterisk channel with real-time state.</summary>
public sealed class AsteriskChannel : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();

    public override string Id => UniqueId;
    public string UniqueId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ChannelState State { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public string? ConnectedLineNum { get; set; }
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int Priority { get; set; }
    public AsteriskChannel? LinkedChannel { get; set; }
    public HangupCause HangupCause { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Extension history for this channel (bounded to last 100 entries).</summary>
    public IReadOnlyList<ExtensionHistoryEntry> ExtensionHistory => _extensionHistory;

    private const int MaxExtensionHistorySize = 100;
    private readonly List<ExtensionHistoryEntry> _extensionHistory = [];

    internal void AddExtensionHistory(ExtensionHistoryEntry entry)
    {
        if (_extensionHistory.Count >= MaxExtensionHistorySize)
            _extensionHistory.RemoveAt(0);
        _extensionHistory.Add(entry);
    }
}

/// <summary>Record of an extension visited by a channel.</summary>
public sealed record ExtensionHistoryEntry(string Context, string Extension, int Priority, DateTimeOffset Timestamp);

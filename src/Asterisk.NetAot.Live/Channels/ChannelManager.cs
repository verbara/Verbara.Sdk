using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Live.Channels;

/// <summary>
/// Tracks all active Asterisk channels in real-time.
/// </summary>
public sealed class ChannelManager
{
    private readonly ConcurrentDictionary<string, AsteriskChannel> _channels = new();
    private readonly ILogger _logger;

    public ChannelManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<AsteriskChannel> ActiveChannels => _channels.Values.ToList().AsReadOnly();

    public AsteriskChannel? GetByName(string name) =>
        _channels.GetValueOrDefault(name);

    public AsteriskChannel? GetByUniqueId(string uniqueId) =>
        _channels.Values.FirstOrDefault(c => c.UniqueId == uniqueId);
}

/// <summary>
/// Represents a live Asterisk channel with real-time state.
/// </summary>
public sealed class AsteriskChannel
{
    public string Name { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public Abstractions.Enums.ChannelState State { get; set; }
    public string? CallerId { get; set; }
    public string? CallerIdName { get; set; }
    public string? ConnectedLineNum { get; set; }
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int Priority { get; set; }
    public AsteriskChannel? LinkedChannel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

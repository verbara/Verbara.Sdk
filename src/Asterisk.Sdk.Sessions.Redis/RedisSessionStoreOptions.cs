namespace Asterisk.Sdk.Sessions.Redis;

/// <summary>
/// Options controlling the behaviour of <see cref="RedisSessionStore"/>.
/// </summary>
public sealed class RedisSessionStoreOptions
{
    /// <summary>
    /// Optional StackExchange.Redis configuration string (e.g. <c>"localhost:6379"</c>). When set and
    /// no <see cref="StackExchange.Redis.IConnectionMultiplexer"/> is already registered, the
    /// <c>UseRedis</c> extension will create a singleton multiplexer from this value. When an
    /// external multiplexer is supplied (via <c>UseRedis(IConnectionMultiplexer, ...)</c>) this
    /// property is ignored.
    /// </summary>
    public string? ConfigurationString { get; set; }

    /// <summary>Key prefix applied to every Redis key written by the store. Default: <c>"ast:"</c>.</summary>
    public string KeyPrefix { get; set; } = "ast:";

    /// <summary>
    /// TTL for completed sessions (and their linked-id index). Sessions older than this that
    /// have reached a terminal state are also pruned from the <c>{prefix}sessions:completed</c>
    /// sorted set on each terminal write. Default: 10 minutes.
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Redis database index. Default: 0.</summary>
    public int DatabaseIndex { get; set; }
}

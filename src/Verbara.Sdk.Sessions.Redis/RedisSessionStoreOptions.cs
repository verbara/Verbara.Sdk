namespace Verbara.Sdk.Sessions.Redis;

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
    /// TTL for completed sessions (and their server's entry in the linked-id index: the index key itself expires
    /// only when every server's session under that linked id has ended, with the latest of them). Sessions older than this that
    /// have reached a terminal state are also pruned from the <c>{prefix}sessions:completed</c>
    /// sorted set on each terminal write. Default: 10 minutes.
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a live session's record, and its server's entry in the linked-id index, stay without being refreshed
    /// before Redis expires them; <see langword="null"/> (the default) gives live records no expiry, as before this
    /// option existed. With it set, the SDK's sessions host refreshes the records of the calls this process holds every
    /// third of this value, so a record outlives its call only when the process that held it is gone: then the record
    /// leaves on its own within this value and no lookup returns it, an active-set member whose record is gone is
    /// removed on the host's next cleanup, and its index entry leaves with the index key, no later than the longer of
    /// this value and <see cref="CompletedRetention"/> after the last save, refresh or ending under that linked id. At
    /// least 30 seconds; 5 minutes is a reasonable value.
    /// <para>
    /// Takes effect only under the SDK's sessions host (<c>AddVerbaraSessions</c> and the other sessions
    /// registrations), which arms the store when it starts; a store built and used without that host gives no live
    /// record an expiry, whatever this value. Set the same value on every process that shares the store, in the same
    /// deploy: a process without it does not refresh its records, and a quiet call of that process can lose its record
    /// until its next save.
    /// </para>
    /// </summary>
    public TimeSpan? ActiveRecordTtl { get; set; }

    /// <summary>Redis database index. Default: 0.</summary>
    public int DatabaseIndex { get; set; }
}

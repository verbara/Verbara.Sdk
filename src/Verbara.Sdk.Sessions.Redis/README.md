# Verbara.Sdk.Sessions.Redis

Redis-backed `SessionStoreBase` implementation for `Verbara.Sdk.Sessions`. Enables horizontal scale-out of call-session state across multiple SDK hosts by persisting sessions to a shared Redis instance.

## Usage

```csharp
services.AddVerbaraSessionsBuilder()
        .UseRedis(opts =>
        {
            opts.ConfigurationString = "localhost:6379";
            opts.KeyPrefix = "myapp:";
            opts.CompletedRetention = TimeSpan.FromMinutes(10);
        });
```

Or supply a pre-built `IConnectionMultiplexer` directly:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
services.AddVerbaraSessionsBuilder()
        .UseRedis(redis, opts => opts.KeyPrefix = "myapp:");
```

## Notes

- AOT-safe: uses source-generated JSON only.
- Active sessions stored as Redis `STRING` keyed by `{prefix}session:{sessionId}` with active set
  `{prefix}sessions:active` and a `linkedid` index `{prefix}idx:linked-by-server:{linkedId}`: a `HASH` with one field
  per server id, so removing or expiring one server's session never hides another server's. A live save writes its
  server's field with `HSET` + `PERSIST` in the save's batch; ending or deleting a session, and pruning during a
  lookup, run a single-key Lua script. Every script names one key, so the store stays valid on Redis Cluster.
- Requirements: Redis 5.0 or later, and a connection whose user may run scripts — `EVALSHA`/`EVAL` and `SCRIPT LOAD`
  (ACL category `@scripting`) plus the commands the scripts call (`TIME`, `HSET`, `HGET`, `HDEL`, `HGETALL`,
  `PERSIST`, `PEXPIREAT`, `GET`, `DEL`, `PEXPIRE`). A deployment or proxy that refuses scripting is not supported:
  ending or deleting a session fails there.
- Saves of one session are not ordered by the store: two concurrent saves of the same session (a live and an ended
  snapshot) can leave the session and its index entry from different saves. Save a session from one place at a time.
- Two lookups by `linkedid`: `GetByLinkedIdAsync(serverId, linkedId, ct)` returns the named server's session;
  `GetByLinkedIdAsync(linkedId, ct)` answers only when exactly one server holds the id, and returns `null` when two or
  more do.
- The string index of 2.10.x and earlier, `{prefix}idx:linked:{linkedId}`, is not migrated: it is still read, never
  written again, and given its retention or removed only when the session it names ends or is deleted.
- Terminal sessions (`Completed`, `Failed`, `TimedOut`) are kept for `CompletedRetention` (default
  10 min) and tracked in the sorted set `{prefix}sessions:completed`.

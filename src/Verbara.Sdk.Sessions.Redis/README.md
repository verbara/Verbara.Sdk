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
  server's field with `HSET` + `PERSIST` in the save's batch; ending or deleting a session runs a single-key Lua
  script. A lookup only reads: it counts an entry while its session key exists and never removes one. Every script
  names one key, so none is refused with `CROSSSLOT` on Redis Cluster. On a Cluster a session key and its index entry
  live in different slots and are not written in order: a lookup that races a session's first save can miss that
  session once and finds it on the next call. The test suite runs against a single node; no Cluster run backs this.
- An index entry whose session key is gone is ignored. An ended entry leaves with its retention. A live entry
  whose session key was removed outside `DeleteAsync`, or by a process that died between the two steps of a delete:
  with `ActiveRecordTtl` set (below), it leaves with the index key's expiry once no process under that `linkedid`
  saves, refreshes or ends a call there (the longer of `ActiveRecordTtl` and `CompletedRetention` after the last of
  them); without the option, it stays, and keeps that `linkedid`'s small hash without an expiry.
- `ActiveRecordTtl` (off by default, at least 30 s; 5 minutes is a reasonable value): under the SDK's sessions host, a
  live session's key and its index key are written with an expiry that the host refreshes every third of the value
  for the calls this process holds, so the record of a call whose process died or restarted expires on its own, and
  the host's cleanup removes its active-set member. Without the host the option does nothing. Set the same value on
  every process that shares the store, in the same deploy. Unset, the store sends exactly the commands it sent before
  the option existed. See the session store guide for the limits.
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

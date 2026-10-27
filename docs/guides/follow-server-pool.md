# Follow servers added to a pool at runtime

`VerbaraServerPool.ServerAdded` announces a usable server after its start and while that exact instance is still in
the pool. `ServerRemoved` announces every actual departure before disposal, including a failed start that an initial
walker could have seen. A listener can therefore receive a removal without having received an addition.

Both events carry `(serverId, server)`. Pair ownership by the server instance. For one id, the old instance's removal
finishes before the replacement's addition. A duplicate id is refused without a notification. Empty or whitespace ids
and the reserved health-data name `status-refused` throw `ArgumentException`; a connecting add rejects them before
creating a connection.

Use this complete protocol when following an existing pool:

1. Initialize one private lock and a dictionary of owned instances and their handlers. Hold that lock while subscribing
   both membership handlers, then release it before enumerating `pool.Servers`.
2. Route both each walk candidate and `ServerAdded` to the same installation routine. Under the private lock, re-check
   `ReferenceEquals(pool.GetServer(id), instance)`. Skip an absent, replaced or already-owned instance. Install the
   instance's subscriptions and record ownership before releasing the lock. Undo all partial subscriptions and ownership
   if installation fails.
3. In `ServerRemoved`, take that same lock and uninstall only the exact owned instance. Ignore an unowned removal within
   this protocol. An old instance's removal must leave its replacement's handlers in place.

The exact-instance re-check skips a server removed before installation. If removal happens after the check, the removal
handler waits for the private lock, then uninstalls the completed installation. Duplicate sightings from the walk and
an added callback install once. The gated unit tests and the runtime agent-login functional test show these cases in
`VerbaraServerPoolMembershipTests` and `PoolMembershipRuntimeTests`.

`GetServer` is a lock-free read. While holding your private lock, use only synchronous instance subscription operations;
do not mutate the pool, await a pool operation or wait on a thread calling the pool. Pool callbacks take locks in the
order membership then subscriber. Reversing that order can deadlock.

Membership callbacks run synchronously under the membership lock. Add, remove and disposal called from that notifying
thread throw `InvalidOperationException` before changing membership or routing. Schedule any required pool change off
the handler and let the handler return. Do not wait for another thread calling the pool from inside the handler.
Each throwing handler produces one Warning with its exception; the operation continues and later handlers still run.
Server and connection disposal run outside the membership lock.

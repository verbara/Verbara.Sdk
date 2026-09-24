# Migrating to the ARI terminal connect state and the surviving accept loop

Required by ADR-0028: a minor that carries a breaking change ships a migration guide.

Two behaviours changed in `Verbara.Sdk.Ari`, both recorded in `Sdk/ADR-0056`. Neither changes an API
signature, so nothing stops compiling. What changes is what the SDK reports, and in one case it moves
an observable that a previous release explicitly told you to watch.

## What you have to do

**Update the package.** There is nothing to edit for most consumers.

Two situations need a look, and both are about code you may have written *because* of the old
behaviour:

1. You read `AriClient.State` after a connect attempt that failed, or you built a timeout around it
   because the state never settled. See below — it settles now.
2. You restart `AriOutboundListener` when it stops accepting, or you watch for that condition. The
   listener no longer stops. See below.

## `AriClient.State` after a failed first connect

**Before:** a first `ConnectAsync` that threw left `State` at `Connecting` for the life of the
instance. The state machine wrote `Connecting`, dialled the events socket, and wrote `Connected` on
the next statement — a throw from the dial skipped that statement, and nothing else could ever write
again, because the events loop starts after the dial and the reconnect loop is only reached from it.

**Now:** the attempt leaves a terminal state, and which one is decided by **who ended the attempt**,
read from your own cancellation token rather than from the exception:

| How the attempt ended | `State` before | `State` now |
|---|---|---|
| Refused upgrade (`401`, `503`), nothing listening, name does not resolve | `Connecting` | `Faulted` |
| You cancelled the token you passed | `Connecting` | `Disconnected` |

**The exception you catch is unchanged** — same type, same message, same stack. The state is written
in a `finally` that catches nothing and runs before the exception becomes observable to the awaiting
caller, so a `catch` block of yours already reads the terminal value.

### What this means for code you may have written

- **A poll or timeout waiting for `State` to leave `Connecting`** can be deleted. It never left
  before; it leaves immediately now.
- **A check that treats `Connecting` as "an attempt is still in progress"** is now correct, where
  before it was permanently wrong after a failed first connect.
- **This amends what 2.5.3 told you.** That release's entry for the credential-refusal fix said an
  initial `ConnectAsync` answered `401` *"still throws `WebSocketException` to the caller and leaves
  `State` at `Connecting`"*, and pointed you at `State` or the health check because observers receive
  no `OnError` when the loop stops. The throw is still exactly that. The state it named is not.
- **`AriHealthCheck` reports the same status.** `Connecting`, `Faulted` and `Disconnected` all fall to
  its `Unhealthy` arm, so a failed first connect was Unhealthy before and is Unhealthy after. Only the
  message text moves, from `"ARI state: Connecting"` to `"ARI state: Faulted"` or
  `"ARI state: Disconnected"`. **If you assert on that string, it changes.**
- **`IsConnected` is unchanged.** It was false under `Connecting` and is false under both terminal
  values.

## `AriOutboundListener` after an accept failure

**Before:** the accept loop wrapped its whole `while` in a `try` whose last clause was
`catch (SocketException) { }`, outside the loop. One transient accept failure while the listener was
meant to be running — `EMFILE`, `ENOBUFS`, a connection aborted in the backlog — ended the loop
silently and for good. `IsRunning` still reported `true`, the socket was still in `LISTEN`, and the
kernel kept completing handshakes nobody would ever read. An outbound connector hung instead of being
refused, and `StartAsync` could not restart the listener because it was still marked running.

**Now:** such a failure is logged at Error, waited out, and the loop keeps accepting. The wait doubles
from 100 ms to a 5 s cap with each consecutive failure and resets after a successful accept.

### What this means for code you may have written

- **A watchdog that restarts the listener** when it notices connections are no longer arriving can be
  removed. It could not work anyway: `StartAsync` refused while `IsRunning` was true.
- **You will see Error lines you did not see before**, under a condition that used to be silent. At
  most twelve a minute once the wait reaches its cap:
  `[AriOutbound] Accept failed — the listener stays bound and accepts again after a backoff`.
  A persistent failure is now noisy on purpose; it was invisible before.
- **A connection that arrives during a wait** stays in the listen backlog until the wait ends, rather
  than being accepted and dropped.
- **The stop path is unchanged.** `StopAsync` still ends the loop at once; it clears the running flag
  before stopping the listener, so an accept aborted by that stop is told apart from a failure by
  `IsRunning` and never by the token.

## How to check which behaviour you are on

Force a connect that cannot succeed — point `AriClientOptions` at a port with nothing listening — and
read `State` in your `catch`:

```csharp
try
{
    await client.ConnectAsync(cancellationToken);
}
catch (Exception)
{
    // 2.5.3 and earlier: Connecting
    // this release:      Faulted
    Console.WriteLine(client.State);
}
```

For the listener, there is nothing to probe safely from a consumer — the condition needs a real
accept failure. The Error line above is the signal that the new behaviour is in play.

# Migrating to an `OnEvent` that awaits every handler

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

One behaviour changed in `Verbara.Sdk.Ami`. No API signature changed, so nothing stops compiling.
`AmiConnection.OnEvent` keeps its type and its `+=`/`-=`; what changes is **how long the event pump waits
for your handlers**, and so how fast a burst of events fills its buffer.

## What happened

`AmiConnection` reads events off the socket into a bounded buffer, the event pump, and a consumer hands
them one at a time to the observers and to `OnEvent`. `OnEvent` was a plain multicast delegate, and
invoking a multicast delegate returns only its **last** handler's `ValueTask`. So, until this release:

- **The pump waited for the last handler only.** Any earlier handler's task was never awaited: a slow
  earlier handler ran in the background while the next events were delivered, effectively
  fire-and-forget.
- **A failing handler went unseen, or stopped everything.** An earlier handler's faulted task was never
  observed. A handler that threw synchronously skipped every handler after it for that event, and a
  throw or a fault that reached the pump ended its consumer: no later event of that session reached any
  handler or observer, and nothing was logged.

## What the SDK does now

- **Every handler is called, in the order it subscribed, before any is awaited** — the same start order
  as before.
- **Then the pump awaits every handler that has not completed, not only the last one**, before it
  delivers the next event. A slow handler now holds delivery **whatever its position**, as only the last
  one did before.
- **A handler that throws, or whose task faults, stops neither delivery nor the other subscribers.** The
  other handlers and the observers receive that event too, and the failing handler receives the next one.
  Each failure is logged once at Warning, with the exception:

  ```
  [AMI_EVENT] OnEvent handler threw on <EventType>
  ```

  and counted once on the new `ami.events.handler_faults` counter (no tags).

The guard is the fix. The await of every handler is the breaking part: a consumer whose earlier handler
was slow never held the pump before, and now does.

## What you have to do

**Update the package.** A consumer with one `OnEvent` handler, or whose handlers all return at once,
has nothing to edit: the last handler was already awaited.

Three situations need a look.

### 1. You have a slow handler that is not the last one

Measured on this change: a first handler that takes about 1 s per event and a second one that returns at
once, then a burst of events. Before this release the pump dropped nothing; now it waits for the slow
handler and the buffer fills:

| `EventPumpCapacity` | burst | `buffer_full` drops before | now |
|---|---|---|---|
| 100 | 1,000 | 0 | 900 (10 of 10 runs) |
| 20,000 (the default) | 25,000 | 0 | 5,000 (10 of 10 runs) |

If that handler must not hold delivery, **hand the work off inside the handler and return**. A bounded
channel read by your own loop keeps the order and makes the backlog yours to size:

```csharp
using System.Threading.Channels;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;

public sealed class HangupArchiver
{
    private readonly Channel<HangupEvent> _work =
        Channel.CreateBounded<HangupEvent>(new BoundedChannelOptions(10_000) { SingleReader = true });

    public HangupArchiver(IAmiConnection ami)
    {
        // Returns as soon as the event is queued: the pump does not wait for the archive.
        ami.OnEvent += evt =>
        {
            if (evt is HangupEvent hangup)
                _work.Writer.TryWrite(hangup);
            return ValueTask.CompletedTask;
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var hangup in _work.Reader.ReadAllAsync(cancellationToken))
        {
            await ArchiveAsync(hangup, cancellationToken); // the slow part, off the pump
        }
    }

    private static Task ArchiveAsync(HangupEvent hangup, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
```

What a full channel does is then your decision (`TryWrite` returning `false`, or `BoundedChannelFullMode`),
not the pump's. Starting a `Task.Run` per event and not awaiting it also returns at once, but it loses the
order and observes no failure.

If the handler's slowness is the point — you want delivery held until it is done — keep it as it is,
and size `EventPumpCapacity` for the longest burst it has to absorb
([high-load-tuning.md](high-load-tuning.md)).

### 2. `ami.events.dropped` with `reason=buffer_full` started after the upgrade

That is the situation above: a handler that was not the last, and so was never awaited, now holds the
pump. Each drop is still one `[AMI_EVENT] Dropped` Warning with the event type, and one measurement on
`ami.events.dropped` tagged `reason=buffer_full`. Time your `OnEvent` handlers to find the slow one —
the `ami.event.dispatch` histogram covers the observers, not the `OnEvent` handlers, so it does not show
it — then apply section 1. These drops are real lost events, not
noise: do not filter them out.

### 3. You alert on handler failures, or relied on a throw ending delivery

A handler that throws is no longer silent and no longer ends the session's delivery. If an alert should
fire when a handler fails, put it on `ami.events.handler_faults > 0` or on the
`[AMI_EVENT] OnEvent handler threw on` Warning. If code relied on a throw to stop receiving events, that
never was a supported way to stop: unsubscribe with `-=`, or end the connection.

## What did not change

- `OnEvent`'s type, `+=` and `-=`. Removing a handler subscribed twice removes its last subscription, as
  a multicast delegate's removal did.
- The order handlers are called in, and that the observers receive each event before `OnEvent`'s
  handlers.
- The pump, its capacity, and what a full buffer does: `[AMI_EVENT] Dropped` and `reason=buffer_full`
  keep their meaning.
- A wrapper of your own that implements `IAmiConnection` and forwards `OnEvent` to an `AmiConnection`
  gets the new behaviour through the connection it forwards to.

## How to check

Subscribe two handlers, the first awaiting a delay longer than the gap between your events, and watch the
log: the first handler's next call starts only after its previous one returned. Make a handler throw
once: one `[AMI_EVENT] OnEvent handler threw on <EventType>` Warning appears, `ami.events.handler_faults`
counts 1 on a `MeterListener` or an OpenTelemetry exporter, and the next events still reach every handler.

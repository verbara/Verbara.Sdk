# Migrating to activity endings that say who ended them

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

One behaviour changed in `Verbara.Sdk.Activities`, in the three base classes every shipped activity
inherits (`ActivityBase`, `AmiActivityBase`, `AriActivityBase`). No API signature changed, so nothing
stops compiling. What changes is the status an activity ends with, and whether `StartAsync` throws,
when **nobody asked it to stop**.

## The rule

An activity ends `Cancelled` only when someone asked it to stop:

- the token you passed to `StartAsync` was cancelled;
- you called `CancelAsync` while it was starting or running;
- you disposed it while it was running.

In those cases `StartAsync` returns without throwing, as before. Every other ending is `Failed`, and
`StartAsync` rethrows the exception. That now includes an `OperationCanceledException` from a deadline
you did not set.

## What changed

| Situation | Before | Now |
|---|---|---|
| A deadline inside the SDK or the transport ends the activity (an AMI response timeout, an ARI HTTP timeout) | `Cancelled`, `StartAsync` returns | `Failed`, `StartAsync` throws the `OperationCanceledException` unchanged |
| `CancelAsync` arrives while the activity is `Starting` | `Cancelled`, then `InProgress`, and the activity executes anyway | `Cancelled`; the activity never executes and `StartAsync` returns |
| After `CancelAsync` or your token's cancellation, the activity's unwind throws something other than a cancellation (an `IOException` from a socket the stop closed, say) | `Failed` next to `Cancelled`, in an order that depends on the host, and `StartAsync` throws it | `Cancelled`, written once; `StartAsync` returns and that exception is dropped |

Unchanged: your token's cancellation, `CancelAsync` on a running activity and disposal mid-run all
still end `Cancelled` without throwing; a failure nobody asked for still ends `Failed` and throws; and
`ExternalMediaActivity` still turns its own `ConnectionTimeout` into a `TimeoutException`.

## Which activities meet a deadline you did not set

- **AMI — `AttendedTransferActivity`.** It awaits `IAmiConnection.SendActionAsync`, whose wait for the
  response is bounded by `AmiConnectionOptions.DefaultResponseTimeout` (2 s by default). When Asterisk
  does not answer in time, the activity used to report `Cancelled`, as if someone had cancelled the
  transfer; it now reports `Failed` and `StartAsync` throws a `TaskCanceledException`.
- **ARI — `SnoopActivity` and `ExternalMediaActivity`.** Their REST calls run on the ARI client's
  `HttpClient`, whose default timeout is 100 s. A request that times out used to end `Cancelled`, and a
  snoop then returned with `SnoopChannel` null; it now ends `Failed` and `StartAsync` throws the
  `TaskCanceledException` (`SnoopChannel` stays null).
- **AGI — the activities on `ActivityBase`.** The SDK's AGI channel has no client-side deadline, so
  they change only if your own `IAgiChannel` or subclass cancels a token of its own.

## What you have to do

1. **Wrap `StartAsync` if a deadline can end the activity.** Code that awaited it bare, because a
   timeout used to return quietly, now sees an exception. Since your own cancellation never throws, an
   `OperationCanceledException` out of `StartAsync` is always a deadline you did not set:

   ```csharp
   var transfer = new AttendedTransferActivity(ami)
   {
       Channel = customerChannelId,
       Destination = new DialPlanExtension("consult", "200"),
   };

   try
   {
       await transfer.StartAsync(cancellationToken);
   }
   catch (OperationCanceledException)
   {
       // Asterisk did not answer within DefaultResponseTimeout; transfer.Status is Failed.
   }
   catch (ActivityFailedException ex)
   {
       // Asterisk refused the Atxfer (Response: Error); ex.Message carries its reason and
       // transfer.Status is Failed.
   }
   ```

   Since 2.9.0 `AttendedTransferActivity` also fails when Asterisk answers the `Atxfer` with
   `Response: Error` (for example `Channel specified does not exist`, or
   `No attended transfer feature code found` when `features.conf` sets no `atxfer` code). It used to
   end `Completed`; now `StartAsync` throws `ActivityFailedException` with Asterisk's message.

2. **Re-read code that branches on `Status` after the await.** `await StartAsync(); if (Status ==
   ActivityStatus.Cancelled)` used to catch timeouts too; now it sees only real requests to stop, and a
   timeout arrives in the `catch` with `Status == Failed`. Dashboards that counted `Cancelled`
   activities will see that share move to `Failed`.

3. **Dereference a snoop's channel only after a successful start.** `await snoop.StartAsync(ct);
   var id = snoop.SnoopChannel!.Id;` failed with a `NullReferenceException` on an HTTP timeout; it now
   fails at the `await` with the `TaskCanceledException`. If you tag failures by exception type, the
   tag changes accordingly.

## A deadline you fold into your own token

The activity can only see the token you pass. A deadline you link into it counts as your request and
ends `Cancelled` without throwing: a `CancellationTokenSource` with `CancelAfter`, a token linked to
one, or the connection token a FastAGI server cancels after its `ConnectionTimeout` when your script
passes it to `StartAsync`. The status then says only that the stop was requested, not that a timer
fired: if you need to tell the two apart, record which one cancelled before it does, or keep the
deadline out of the token you pass.

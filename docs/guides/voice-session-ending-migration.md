# Migrating to a voice session that ends when its handler does

A Voice AI session handed out by `VoiceAiSessionBroker` now ends when its handler does. When
`ISessionHandler.HandleSessionAsync` returns, throws, or the Realtime vendor closes the conversation, the broker
hangs the AudioSocket line up. Before, the line stayed open in silence until the caller gave up. Nothing stops
compiling, but seven behaviours move:

- the end of the line;
- the host's graceful shutdown, which now waits for the handlers;
- `AudioSocketSession.HangupAsync`, which no longer throws on an ended session;
- the AudioSocket server's start and stop;
- `MaxConcurrentSessions`, which is no longer passed under a burst;
- the meaning of `tts.syntheses.completed`;
- two new `…cancelled` counters.

## What you have to do

**Update the packages** (`Verbara.Sdk.VoiceAi`, `Verbara.Sdk.VoiceAi.AudioSocket`,
`Verbara.Sdk.VoiceAi.OpenAiRealtime` move together). Then look at the places below where your code may depend on
the old behaviour:

1. A custom `ISessionHandler` that returns before it has finished with the session, for example one that starts a
   background task and returns. It now loses its line. See [handlers that return early](#a-handler-that-returns-early).
2. A host that subscribes its own `AudioSocketServer.OnSessionStarted` handler next to the broker's. See
   [two subscribers on one server](#two-subscribers-on-one-server).
3. Code that calls `VoiceAiSessionBroker.StopAsync` directly, or that relied on a fast shutdown with calls up. See
   [the shutdown](#the-shutdown-waits-for-the-handlers).
4. Code that catches `ObjectDisposedException` from `HangupAsync` to learn that the call was gone. See
   [`HangupAsync`](#hangupasync-on-an-ended-session).
5. Code that starts an `AudioSocketServer` again after stopping it. See [the server's start and
   stop](#the-audiosocket-servers-start-and-stop).
6. Dashboards and alerts on `tts.syntheses.completed`, or on the in-flight arithmetic of the STT and TTS counters.
   See [the counters](#the-stt-and-tts-counters).
7. A deployment sized close to `MaxConcurrentSessions`. See [the limit](#the-session-limit-under-a-burst).

## The line when the handler is done

**Before:** the broker started the handler and did nothing when the handler finished. A handler that returned
without calling `HangupAsync`, a handler that threw, and the Realtime bridge after the vendor closed the
conversation all left the AudioSocket connection open. The caller heard silence and the call held its channel until
the caller hung up. Asterisk did not end it on its own either: still open after 300 s.

**Now:** as soon as `HandleSessionAsync` completes, whether it returned, threw or was cancelled, the broker calls the
session's `HangupAsync`. That writes one hangup frame if the session is still live, then closes the connection. What
Asterisk does next depends on how the call reached the bot:

| Route | What follows the hangup frame |
|---|---|
| `AudioSocket()` dialplan application, Asterisk 20 and later | `AudioSocket()` returns and the dialplan goes on at the next priority |
| ARI `externalMedia` with `encapsulation=audiosocket` | the external-media channel leaves Stasis (`StasisEnd`); the caller stays in your application's bridge, and your application decides what follows |
| `AudioSocket()` on Asterisk 18 | the application fails and the call is hung up, as for any end from the server |

A handler that already hung up, or a caller that hung up first, gets nothing more: the far end receives at most one
hangup frame per session. A handler that throws is still logged once at Error (`VoiceAi session error`), as before.

**Measured** on 2026-10-02, against Asterisk 20.20.1, 22.9.0 and 23.4.1, with the SDK's own server and broker. Each
case ran 20 calls per route and version, with a handler that played 3 s of tone and then returned, threw, or ran
the Realtime bridge against a vendor that closed the conversation. Each call was then watched for 15 s:

| | 2.6.1 | Now |
|---|---|---|
| Lines still open at the end of the watch | 360 of 360 | 0 of 360 |
| Session ended after the handler finished | never, within the watch | 360 of 360, within 8 ms |
| `AudioSocket()` route: the dialplan went on | 0 of 180 | 180 of 180 |
| `externalMedia` route: `StasisEnd` on the AudioSocket channel | 0 of 180 | 180 of 180, the caller still bridged in 180 |
| What the caller heard after the tone (`AudioSocket()` route) | 15 s of silence, until the test hung up | no silence: the call left the bot when the tone ended |

On Asterisk 18.26.4, measured the same day as information, the session ended 120 of 120 times. On the
`AudioSocket()` route the application failed and the call was hung up 60 of 60 times. On the `externalMedia` route,
`StasisEnd` arrived 60 of 60 times.

A session the broker ends while it is still live leaves one line at Information:
`VoiceAi session [<channel id>] ended by the broker while the line was still live`. A session that the caller or the
handler had already ended leaves none.

### A handler that returns early

The rule is the one `ISessionHandler` always stated, that the method runs the session. It is now enforced: a
handler keeps its line exactly as long as `HandleSessionAsync` runs. Await the work instead of leaving it in the
background:

```csharp
// Before: returned at once and kept talking from a background task. Now the line is hung up on return.
public ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
{
    _ = Task.Run(() => TalkAsync(session, ct));
    return ValueTask.CompletedTask;
}

// After: the session lasts as long as the conversation does.
public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
{
    await TalkAsync(session, ct);
}
```

A handler that returns while a task of its own still writes audio breaks the same contract in a second way: the
broker's hangup can land between two of those writes. The session writes one frame at a time, so the far end never
reads a torn frame, but the leftover task's next write then throws `ObjectDisposedException`. The SDK's own handlers
(`VoiceAiPipeline`, `OpenAiRealtimeBridge`) wait for every loop they start before they return.

To hand the call back to the dialplan before the conversation is over, call `HangupAsync` yourself and then return.

### Two subscribers on one server

`AudioSocketServer.OnSessionStarted` raises every subscriber with the same session. If your host subscribes its own
handler next to the broker's (`AddVoiceAiPipeline` or `AddOpenAiRealtimeBridge`), the broker's ending ends that
session for your handler too, as soon as the broker's handler is done. Move your work into the `ISessionHandler`, or
run the two on separate servers.

## The shutdown waits for the handlers

**Before:** the broker's `StopAsync` unsubscribed from the server and returned at once. The handlers kept running
until the AudioSocket server's own stop closed their sessions (without a hangup frame), or until the host's shutdown
budget ran out.

**Now:** a graceful stop, one whose token is not cancelled, ends every live session the broker handed out. It writes
a hangup frame, then closes, so each call goes on in the dialplan as above. Then it waits for the handlers to return.
It does not cancel the token the handlers run under. Once the host's stop token is cancelled, because the shutdown
budget (`HostOptions.ShutdownTimeout`) ran out, the stop cancels the handlers' token and returns
without waiting any further. `Dispose` does not wait: it cancels the handlers' token and returns, as before.

What this means for code you may have written:

- **A host shutdown with calls up** now ends each call with a hangup frame and takes as long as the handlers need to
  see their sessions end and return, within the host's budget.
- **A direct call** to `VoiceAiSessionBroker.StopAsync(CancellationToken.None)` now waits until every handler has
  returned, however long that takes. A handler that ignores both its session ending and its token blocks it for
  good. Pass a token you cancel after a bound of your own:

  ```csharp
  using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
  await broker.StopAsync(budget.Token);
  ```

- **Register the server before the pipeline.** A host stops its hosted services in the reverse order of their
  registration. With `AddAudioSocketServer` called first, as the package READMEs show, the broker stops first and
  ends every call with a hangup frame. If the server is stopped first, or concurrently
  (`HostOptions.ServicesStopConcurrently`), it closes the sessions without a frame. On Asterisk 20 and later a close
  with the caller's audio unread fails the call instead of continuing the dialplan. `AddOpenAiRealtimeBridge` already
  requires the server to be registered first.

The broker is not restartable, as before. A `StartAsync` after a stop subscribes nothing. A `StartAsync` after
`Dispose` throws `ObjectDisposedException`. A `StopAsync` after `Dispose` does nothing; before, it could throw
`ObjectDisposedException`.

## `HangupAsync` on an ended session

**Before:** `AudioSocketSession.HangupAsync` threw `ObjectDisposedException` on a session that had already ended.
That covered a caller who hung up first, a second call, and a session its owner had disposed.

**Now:** it completes and writes nothing. It is idempotent, so code may call it on every way out without checking
`IsConnected`. When the transport fails under the frame, because the far end is already gone, the frame is skipped
and the session still ends. A hangup issued while an audio write is in flight waits for that write, so the far end
reads the whole audio frame and then the whole hangup frame. Cancelling its token while it waits for that write
abandons the hangup with an `OperationCanceledException`.

Measured on loopback, 20 runs each: a second `HangupAsync` completed 20 of 20 times, with one hangup frame on the
wire. A `HangupAsync` after the caller hung up first completed 20 of 20 times, with no frame. Both threw
`ObjectDisposedException` 20 of 20 times before.

If you caught `ObjectDisposedException` from `HangupAsync` to learn that the call was gone, read
`session.IsConnected` before the call, or subscribe to `OnHangup`.

## The AudioSocket server's start and stop

**Before:** every `StartAsync` bound a new listener and started a new accept loop. A server started twice, for
example once as the hosted service `AddAudioSocketServer` registers and again by hand, kept serving on the first port
after its stop. A `StopAsync` after `DisposeAsync` threw `ObjectDisposedException`.

**Now:** the server binds one listener and runs one accept loop in its lifetime:

| Call | Now |
|---|---|
| `StartAsync` while running | completes, binds nothing; the first listener stays |
| `StartAsync` after `StopAsync` | completes, binds nothing: the server is not restartable |
| `StartAsync` after `DisposeAsync` | throws `ObjectDisposedException` |
| `StopAsync` after `DisposeAsync` | completes, does nothing |

**A start after a stop no longer restarts the server.** If your code stops a server and starts it again, create a
new `AudioSocketServer` instead. A host that starts a shared server from two places keeps working: the second start
changes nothing, and one stop releases everything.

## The session limit under a burst

**Before:** `MaxConcurrentSessions` was checked separately from the registration of the session. A burst of calls
that identified themselves at the same moment could all pass the check before any of them registered, so the server
ran past its limit.

**Now:** each served channel id takes its place in the same atomic step that checks the limit. A burst admits exactly
the limit and refuses the rest. Each refused call receives a hangup frame, so on Asterisk 20 and later its dialplan
goes on after `AudioSocket()`, and the server logs `Session limit reached` at Warning. A call that comes back with
the UUID of a session still ending shares that session's place, as before.

A deployment that ran past its limit in bursts now refuses calls it used to admit. Size `MaxConcurrentSessions` for
the peak you want served, not for the average; see [high-load-tuning.md](high-load-tuning.md).

Nothing is registered once the server's stop has begun. A connection that identified itself just before the stop
is refused with a hangup frame and never announced. Before, it could be registered after the stop had ended
every session, and stay counted, with no frame.

## The STT and TTS counters

**Before:** a recognition or a synthesis cut short by the session's token (the host's cancel, or the broker's
cancellation of its handlers) was counted in no bucket at all. So `started - completed - failed` grew with every
cancelled turn. A synthesis cut short by a barge-in, by the pipeline's disposal or by the caller hanging up
mid-playback was counted in `tts.syntheses.completed`.

**Now:** every recognition and every synthesis the pipeline starts is counted in exactly one of `completed`,
`failed` or the new `cancelled`, so per arm `started - completed - failed - cancelled` is what is still in flight.

| Instrument | Meter | What it counts | `voiceai.ending` tag |
|---|---|---|---|
| `stt.transcriptions.cancelled` (`SpeechRecognitionMetrics.TranscriptionsCancelled`) | `Verbara.Sdk.VoiceAi.Stt` | transcriptions cut short by the session's token | `session-cancelled` |
| `tts.syntheses.cancelled` (`SpeechSynthesisMetrics.SynthesesCancelled`) | `Verbara.Sdk.VoiceAi.Tts` | syntheses cut short by someone outside the synthesizer | `session-cancelled`, `barge-in`, `disposal` (the pipeline was disposed) or `far-end` (a write found the session already ended) |

`tts.syntheses.completed` now counts only a synthesis whose audio was all written to the session. To keep a
dashboard on the old number:

```text
old tts.syntheses.completed = tts.syntheses.completed
                            + tts.syntheses.cancelled{voiceai.ending="barge-in"}
                            + tts.syntheses.cancelled{voiceai.ending="disposal"}
                            + tts.syntheses.cancelled{voiceai.ending="far-end"}
```

`stt.transcriptions.completed` and every `failed` counter keep their meaning. The latency histograms,
`tts.syntheses.silent` and `SynthesisEndedEvent` are unchanged.

## The Realtime bridge

What the caller hears when the vendor closes the conversation follows from [the line](#the-line-when-the-handler-is-done):
on the `AudioSocket()` route of Asterisk 20 and later, the call goes on in the dialplan the moment the bridge returns
(60 of 60 calls in the measurement above, within 2 ms). On the `externalMedia` route the channel leaves Stasis with
the caller still in your bridge (60 of 60). Before, the caller heard silence until they hung up.

Two shutdown faults are also gone, with no change asked of you:

- A Realtime session that the host cancels after the caller hung up returns only once both of its audio loops have
  ended. Before, a loop could keep running over resources the session had released and fault, unobserved, with
  `ObjectDisposedException`.
- An event that a session raises after the bridge was disposed, such as a function result that arrives during
  shutdown, is dropped and logged at Debug. Before, it threw inside the session and was counted in
  `openai_realtime.sessions.failed`.

## What did not change

- No public signature changes. The only additions are the two counters above.
- A handler that runs until the caller hangs up behaves as before: the caller's hangup ends the session, and the
  broker adds no frame.
- A handler that calls `HangupAsync` and then returns sends one hangup frame, as before.
- The handler's token is still cancelled only by a stop that is no longer graceful, or by `Dispose`.
- The same-UUID re-entry wait (up to 1 second), and the hangup frame on every refusal.

## How to check

Place a test call through `AudioSocket()` with a `Set()` or `NoOp()` after it, and let the bot finish its turn.
The log shows `ended by the broker while the line was still live`, and the dialplan priority after `AudioSocket()`
runs at once. Before, the call sat in silence. Then stop the host with a call up. The call goes on in the dialplan
and the stop returns once the handler has returned. For the counters, compare `started` against
`completed + failed + cancelled` per arm after a load test: the difference is what is still in flight, and zero once
the host is idle.

# Migrating to a bounded Realtime function call and speech-provider timeouts that must be usable

A minor release that carries a breaking change ships a migration guide.

Two waits in the Voice AI packages now have the bound they were given. A function tool of the OpenAI Realtime
bridge that never returns no longer holds the call in silence: `OpenAiRealtimeOptions.FunctionCallTimeout`, new,
bounds it. And the speech providers' connect and HTTP timeouts accept only a usable number of seconds, 1 to 600,
where 0 failed every call and a negative value threw from deep inside a timer. Nothing stops compiling, but
four behaviours move:

- a Realtime function call that outlasts 30 s is answered as timed out;
- a conversation the host cancels no longer waits for a function still running;
- a `ConnectTimeoutSeconds` (or LMNT's `HttpTimeoutSeconds`) outside 1–600 is rejected, naming the option;
- Deepgram speech-to-text and ElevenLabs options gain a validator, which also enforces their `BaseUri`.

## What you have to do

**Update the packages** (`Verbara.Sdk.VoiceAi`, `Verbara.Sdk.VoiceAi.OpenAiRealtime`, `Verbara.Sdk.VoiceAi.Stt`,
`Verbara.Sdk.VoiceAi.Tts`). Then look at the places below where your configuration or code may depend on the old
behaviour:

1. A Realtime function tool that can legitimately run longer than 30 s. Raise `FunctionCallTimeout`. See
   [the function-call bound](#a-realtime-function-call-is-bounded).
2. Dashboards or audits built on `RealtimeFunctionCalledEvent`. A timed-out call is published once with
   `{"error":"timeout"}`. See [what a timed-out call looks like](#what-a-timed-out-call-looks-like).
3. A speech provider configured with a `ConnectTimeoutSeconds` of 0, a negative value or more than 600, or an
   LMNT `HttpTimeoutSeconds` outside that range. See [the provider timeouts](#the-speech-provider-timeouts).
4. A Deepgram speech-to-text or ElevenLabs `BaseUri` that does not start with `ws://` or `wss://`. See
   [the two new validators](#two-new-validators-and-their-baseuri-check).

## A Realtime function call is bounded

**Before (2.6.1 and earlier):** the bridge awaited an `IRealtimeFunctionHandler.ExecuteAsync` with no limit.
While a function runs the session reads nothing from OpenAI, so a function that ignored its token and never
returned (an HTTP call to a CRM with no timeout of its own, a lock never released) held the caller in silence
for the rest of the call, and after the caller hung up it kept the session open until the function returned.

**Now:** `OpenAiRealtimeOptions.FunctionCallTimeout` bounds each call. It defaults to **30 seconds**. When the
bound elapses before the function returns, the bridge:

1. answers the call with the function-call output `{"error":"timeout"}` followed by a `response.create`, so the
   model can tell the caller (nothing is sent once the caller has hung up);
2. logs one Warning:
   `[<channel>] Function '<name>' did not return within its FunctionCallTimeout of <ms> ms; answered as timed out and no longer awaited`;
3. adds one to the new counter `openai_realtime.function_calls.timed_out` (the call is also counted in
   `openai_realtime.function_calls.total`, as before);
4. publishes the call's `RealtimeFunctionCalledEvent` once, with `ResultJson` = `{"error":"timeout"}`;
5. cancels the `CancellationToken` it handed to `ExecuteAsync`, so a function that honours its token can stop
   and release what it holds;
6. goes on with the session without waiting for the function any longer.

The token is cancelled only after the bridge has decided the call timed out, so a function that honours it and
throws `OperationCanceledException` is still answered `{"error":"timeout"}` and counted once. Whatever a function
returns or throws after the bound is logged at Debug and never sent. A function that returns at exactly the bound
is answered as timed out. A function that returns inside the bound is answered with its result, as before, with
no Warning.

The session's own token is not the one cancelled: a hang-up still does not cancel a running function. If the
caller hangs up while a function runs, the bridge still waits for it, now only up to its bound; at the bound it
logs, counts and publishes as above, sends nothing, and the 10 s wait for OpenAI's close answer restarts from
that moment, as it does from a function's return.

### A conversation the host cancels

**Before:** a conversation the host cancelled (a shutdown past its graceful phase) waited for a function still
running before it returned.

**Now:** it returns once both of its audio loops have ended, without waiting for the function. The function is
abandoned as above (its token cancelled, a late result logged at Debug), with no Warning, no count and no event,
because the host, not the bound, ended it.

### Setting the bound

`FunctionCallTimeout` accepts more than zero and at most `int.MaxValue` milliseconds (about 24.8 days). Any other
value, `Timeout.InfiniteTimeSpan` included, is rejected naming the option:

| Where | When |
|---|---|
| The options validator `AddOpenAiRealtimeBridge` registers | when the options are first resolved, that is, when the bridge is first created |
| The bridge's constructor | `ArgumentOutOfRangeException`, `ParamName` = `FunctionCallTimeout`, for options built without the validator (`Options.Create`, a hand-made registration) |
| Before each function call | the same exception, for a value changed on the options object after the bridge was built; the call is not started |

```csharp
services.AddOpenAiRealtimeBridge(opts =>
{
    // ...
    opts.FunctionCallTimeout = TimeSpan.FromSeconds(90);
});
```

**In configuration, write it as `hh:mm:ss`.** `"FunctionCallTimeout": "00:01:30"` is 90 s. A bare number binds
as days: `"5"` is five days, not five seconds.

### What a timed-out call looks like

| Signal | A call that returned | A call that timed out |
|---|---|---|
| Function-call output sent to OpenAI | the result | `{"error":"timeout"}` |
| `RealtimeFunctionCalledEvent.ResultJson` | the result | `{"error":"timeout"}`, published once at the bound |
| `openai_realtime.function_calls.total` | +1 | +1 |
| `openai_realtime.function_calls.timed_out` | — | +1 |
| Log | — | one Warning, and a Debug line if the function ends later |

Alert on `openai_realtime.function_calls.timed_out`: each one is a caller who waited the whole bound and then
heard the model say the function failed.

## The speech-provider timeouts

**Before (2.6.1 and earlier):** each provider's `ConnectTimeoutSeconds` was documented as "must be positive" and
checked by nothing. On the WebSocket providers, `0` failed every call as a handshake failure that looked like a
refused upgrade, and `-1` threw from inside a timer, naming no option. On Speechmatics text-to-speech and LMNT over
HTTP, where the value is the HTTP client's timeout, `0` and `-1` threw from `HttpClient` naming its own `Timeout`
setter.

**Now:** these options accept whole seconds from **1 to 600**:

| Package | Options | Property | Default |
|---|---|---|---|
| `Verbara.Sdk.VoiceAi.Stt` | `DeepgramOptions`, `AssemblyAiOptions`, `CartesiaOptions`, `SpeechmaticsOptions` | `ConnectTimeoutSeconds` | 5 |
| `Verbara.Sdk.VoiceAi.Tts` | `ElevenLabsOptions`, `CartesiaOptions`, `DeepgramTtsOptions`, `LmntTtsOptions` | `ConnectTimeoutSeconds` | 5 |
| `Verbara.Sdk.VoiceAi.Tts` | `SpeechmaticsOptions` (an HTTP timeout) | `ConnectTimeoutSeconds` | 10 |
| `Verbara.Sdk.VoiceAi.Tts` | `LmntTtsOptions` | `HttpTimeoutSeconds` | 30 |

A value outside the range is rejected in three places, each naming the option:

| Where | What you see | When |
|---|---|---|
| The provider's options validator, registered by the SDK's `Add…` method | an `OptionsValidationException` naming the property | when the provider's options are first resolved: usually when the provider is first created, on the first call that uses it. **Not at host start-up**: no speech-provider registration calls `ValidateOnStart` |
| The provider's public constructor | `ArgumentOutOfRangeException` with `ParamName` = `ConnectTimeoutSeconds` (or `HttpTimeoutSeconds`) | for options built without the validator, such as `Options.Create` or a registration of your own |
| Right before each connect | the same `ArgumentOutOfRangeException` | for a value changed on the options object after the provider was built; nothing is dialled |

LMNT has two timeouts, one per transport. Its validator checks both whatever `Transport` is set. Its constructor
and its connect check only the one the transport uses: `ConnectTimeoutSeconds` on `LmntTransport.WebSocket`,
`HttpTimeoutSeconds` on `LmntTransport.Http`, before the HTTP client is built. So an LMNT client on HTTP with an
unusable `ConnectTimeoutSeconds` is rejected by the validator only; a client you build yourself with that value
keeps working, because it never uses it. Speechmatics text-to-speech checks its `ConnectTimeoutSeconds` before it
builds its HTTP client.

To raise a slow network's connect limit, set the property to any value up to 600; there is no longer a value that
means "no limit".

## Two new validators and their `BaseUri` check

Seven of the nine providers above already shipped a source-generated options validator. The other two gain one,
public, next to their options, and registered by the SDK's registration the same way:

| Validator | Registered by |
|---|---|
| `Verbara.Sdk.VoiceAi.Stt.Deepgram.DeepgramOptionsValidator` | `AddDeepgramSpeechRecognizer` |
| `Verbara.Sdk.VoiceAi.Tts.ElevenLabs.ElevenLabsOptionsValidator` | `AddElevenLabsSpeechSynthesizer` |

Besides the 1–600 range, each validator enforces the rule both options already declared and nothing checked:
**`BaseUri` must start with `ws://` or `wss://`**. A host whose `BaseUri` is, for example, `https://…` now fails
when the provider's options are first resolved, with an `OptionsValidationException` naming `BaseUri`. Before,
nothing checked it until the client tried to connect. Fix the scheme.

If you register these providers yourself, with `Configure<DeepgramOptions>` or `Configure<ElevenLabsOptions>` and
your own service registration instead of the SDK's `Add…` method, no validator runs; register it yourself to get
the same checks at resolution:

```csharp
services.AddSingleton<IValidateOptions<DeepgramOptions>, DeepgramOptionsValidator>();
services.AddSingleton<IValidateOptions<ElevenLabsOptions>, ElevenLabsOptionsValidator>();
```

The constructors' range check reaches such a registration either way.

## How to check which behaviour you are on

Register a Realtime function whose `ExecuteAsync` awaits `Task.Delay(Timeout.Infinite)` without its token, set
`FunctionCallTimeout` to `00:00:02`, and ask the model to call it. On this release the bridge sends
`{"error":"timeout"}` as the function's output after about 2 s and logs one
`[<channel>] Function '<name>' did not return within its FunctionCallTimeout` Warning. On 2.6.1 the option does
not exist and the call stays silent.

For the providers, set `ConnectTimeoutSeconds = 0` and resolve the provider: this release throws
`OptionsValidationException` naming `ConnectTimeoutSeconds`; 2.6.1 builds it and fails on the first connect.

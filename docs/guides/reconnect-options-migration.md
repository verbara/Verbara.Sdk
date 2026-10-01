# Migrating to reconnect options that mean what they say

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

Three behaviours changed in `Verbara.Sdk.Ami`, `Verbara.Sdk.Ari` and `Verbara.Sdk.Hosting`. No API signature
changed, so nothing stops compiling. What changes is **which reconnect values are accepted**, **how many reconnect
attempts `MaxReconnectAttempts` makes** on AMI, and **which options `AddVerbara` actually hands to the clients**.

## What happened

- **An unusable reconnect value was accepted.** A `ReconnectMultiplier` below 1, a `ReconnectMaxDelay` below
  `ReconnectInitialDelay` or a negative delay passed start-up and construction. The backoff threw on it only after a
  connection was lost, inside the reconnect loop, where nothing observed the exception: the loop ended and nothing
  was logged.
- **AMI made one reconnect attempt fewer than asked.** The limit was checked after the backoff and before the
  connect, so `MaxReconnectAttempts = N` made N − 1 attempts, and `MaxReconnectAttempts = 1` never reconnected.
- **`AddVerbara` dropped most options.** 12 of the 18 AMI options and 5 of the 9 ARI options never reached the
  client: they were silently left at their defaults, whether you set them inline with `AddVerbara(o => …)` or
  under the `Asterisk:Ami` / `Asterisk:Ari` sections. For example, `MaxReconnectAttempts = 3` reached the client
  as `0`, which means retry forever. A malformed value under those sections was skipped and the default used.

## What the SDK does now

### 1. An unusable reconnect value is rejected up front

With `AutoReconnect` on, these values are refused, and the error names the option:

- `ReconnectMultiplier` below `1.0`, or not a finite number (NaN, ∞);
- `ReconnectMaxDelay` below `ReconnectInitialDelay`;
- a negative `ReconnectInitialDelay` or `ReconnectMaxDelay`;
- a delay above `int.MaxValue` milliseconds (about 24.8 days, the longest delay .NET can wait);
- a negative ARI `MaxReconnectAttempts`.

Where it is refused:

- **Through `AddVerbara`** (or any registration that validates on start), the host's start fails with an
  `OptionsValidationException` naming the option.
- **On every construction path**: the `AmiConnection` and `AriClient` constructors, and so `AmiConnectionFactory`,
  `AriClientFactory` and `VerbaraServerPool.AddServerAsync`, throw `ArgumentOutOfRangeException` whose `ParamName`
  is the option.

With `AutoReconnect` off, none of these values is checked, except AMI's `MaxReconnectAttempts`, which must be 0 or
more, as before.

If the backoff still fails inside the loop (the options are held by reference and can be changed after
construction), the loop no longer dies silently. It logs once at Error:

```
[AMI] Reconnect backoff failed: the reconnect loop ends
[ARI] Reconnect backoff failed: the reconnect loop ends
```

(event name `ReconnectBackoffFailed`, with the exception) and ends as a give-up does: AMI reads `Disconnected`,
ARI reads `Faulted`.

### 2. AMI `MaxReconnectAttempts = N` makes N reconnect attempts

The limit is now checked before the backoff, so N means N, and the give-up follows the last failed attempt with no
further delay. `MaxReconnectAttempts = 0` still means unlimited.

Measured against Asterisk 20, 22 and 23, restarted with the AMI user's credentials rejected and `N = 4`:

| | reconnect connects | from Asterisk's restart to `Disconnected`, with 1 s ×2 |
|---|---|---|
| before | 3, in 60 of 60 runs | 17.2–17.6 s |
| now | 4, in 60 of 60 runs | 17.2–18.8 s |

"1 s ×2" is a 1 s `ReconnectInitialDelay` with a `ReconnectMultiplier` of 2. The runs covered more than one backoff
configuration; the connect count held in all of them.

ARI's `MaxReconnectAttempts` already made N attempts and is unchanged.

### 3. Every option you set through `AddVerbara` reaches the client

`AddVerbara(o => …)` copies every AMI and ARI option, and `AddVerbara(builder.Configuration)` binds every option of
`AmiConnectionOptions` from `Asterisk:Ami` and of `AriClientOptions` from `Asterisk:Ari`, by name. Two consequences:

- **A `Configure<AmiConnectionOptions>` or `Configure<AriClientOptions>` placed *before* `AddVerbara` no longer
  survives.** `AddVerbara` now writes every option, so it replaces what the earlier `Configure` set. One placed
  *after* `AddVerbara` still wins, as before.
- **A malformed value under those sections fails.** `AddVerbara` throws an `InvalidOperationException` naming its
  key, instead of silently using the default. Values are read with the invariant culture (`1.5`, not `1,5`).

`Verbara.Sdk.Hosting` now depends on `Microsoft.Extensions.Configuration.Binder`.

## What you have to do

**Update the package**, then check the three situations below.

### 1. Start-up or construction now fails naming a reconnect option

Fix the value the exception names. The accepted ranges are in
[high-load-tuning.md](high-load-tuning.md#reconnection-tuning). For example, a `ReconnectMaxDelay` of
`"00:00:02"` under a `ReconnectInitialDelay` of `"00:00:05"` was accepted before, and the reconnect loop died
silently after the first loss. Now the start fails naming `ReconnectMaxDelay`; raise it to at least the initial
delay:

```json
{
  "Asterisk": {
    "Ami": {
      "ReconnectInitialDelay": "00:00:05",
      "ReconnectMaxDelay": "00:00:30"
    }
  }
}
```

If you need no reconnect at all, set `AutoReconnect` to `false` instead of an unusable value.

### 2. You set AMI `MaxReconnectAttempts` to a positive number

The connection now makes one more reconnect attempt before giving up. To keep the old number of attempts, set N − 1:

```csharp
// Before: made 3 reconnect attempts
options.MaxReconnectAttempts = 4;

// After: the same 3 attempts
options.MaxReconnectAttempts = 3;
```

If you set it through `AddVerbara`, it never arrived before (situation 3), so your connection retried forever. It
now makes exactly the number you set and then reads `Disconnected`.

### 3. You configure through `AddVerbara`

**Check that the values you set are the ones you want.** Options you set long ago, and that never took effect, now
do: a `MaxReconnectAttempts` that now gives up, an `EventPumpCapacity`, heartbeat or timeout values, or an ARI
`AutoReconnect = false`.

**Move any `Configure<T>` after `AddVerbara`:**

```csharp
// Before: worked only because AddVerbara did not copy MaxReconnectAttempts
builder.Services.Configure<AmiConnectionOptions>(o => o.MaxReconnectAttempts = 10);
builder.Services.AddVerbara(builder.Configuration);

// After: set it after AddVerbara (or set it in AddVerbara itself, or under Asterisk:Ami)
builder.Services.AddVerbara(builder.Configuration);
builder.Services.Configure<AmiConnectionOptions>(o => o.MaxReconnectAttempts = 10);
```

**Fix any key the error names.** A value that cannot be read as the option's type now stops `AddVerbara`.
`"MaxReconnectAttempts": "ten"` used to be skipped, leaving the default `0` (unlimited); now it throws an
`InvalidOperationException` naming the key. Write it as a number:

```json
{
  "Asterisk": {
    "Ami": {
      "MaxReconnectAttempts": 10
    }
  }
}
```

## What did not change

- The option names, their types and their defaults.
- `MaxReconnectAttempts = 0` means unlimited, on both clients.
- With `AutoReconnect` off, nothing new is checked.
- ARI's number of reconnect attempts.
- A `Configure<T>` placed after `AddVerbara` still has the last word.

## How to check

Start the host: if it starts, every reconnect value is usable. Then, with an AMI connection up and
`MaxReconnectAttempts = N`, restart Asterisk with the AMI user's credentials rejected: the log shows N `[AMI] Reconnecting` and N `[AMI] Reconnect attempt failed` lines before
the connection reads `Disconnected`. To confirm `AddVerbara` delivered your values, resolve
`IOptions<AmiConnectionOptions>` (or `IOptions<AriClientOptions>`) from the built provider and read them.

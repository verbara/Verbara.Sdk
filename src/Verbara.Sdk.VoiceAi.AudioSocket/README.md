# Verbara.Sdk.VoiceAi.AudioSocket

AudioSocket transport for Verbara.Sdk.VoiceAi — bridges Asterisk AudioSocket streams into the Voice AI pipeline.

## Installation

```bash
dotnet add package Verbara.Sdk.VoiceAi.AudioSocket
```

## Quick Start

```csharp
// Register as a hosted service (DI lifecycle managed)
services.AddAudioSocketServer(opts =>
{
    opts.ListenAddress = "0.0.0.0";
    opts.Port = 9092;
    opts.MaxConcurrentSessions = 100;
});

// Handle sessions manually (without VoiceAi pipeline)
var server = app.Services.GetRequiredService<AudioSocketServer>();
server.OnSessionStarted += async session =>
{
    await foreach (var frame in session.ReadAudioAsync(ct))
    {
        // process 20 ms PCM16 frames from Asterisk
    }
    await session.WriteAudioAsync(responseAudio, ct);
};
```

## Features

- `AudioSocketServer` — `IHostedService` TCP server; accepts Asterisk AudioSocket connections
- `AudioSocketSession` — per-call session with `ReadAudioAsync()` and `WriteAudioAsync()` for 20 ms PCM16 frames, and an idempotent `HangupAsync()`
- UUID handshake: automatically reads the channel UUID frame from Asterisk on connection
- `OnSessionStarted` event for routing sessions to custom handlers
- `AddAudioSocketServer()` DI extension for one-line registration
- Zero-copy `System.IO.Pipelines` framing; Native AOT compatible

## Ending a session

`HangupAsync` writes one hangup frame and closes the connection. On Asterisk 20 and later the call
then goes on in the dialplan after `AudioSocket()`, where a bare close with the caller's audio unread
fails the call; on Asterisk 18 any end from the server fails the application.

- **Idempotent.** On a session that has already ended (the caller hung up, it was hung up before, or
  its owner disposed it) `HangupAsync` completes and writes nothing, so the far end receives at most
  one hangup frame per session. Call it on every way out without checking `IsConnected` first.
- **Never inside another frame.** Writes are serialised per session: a hangup issued while an audio
  write is in flight waits for that write, so the far end reads whole frames only. Cancelling the
  hangup's token while it waits abandons it with an `OperationCanceledException`.
- With `Verbara.Sdk.VoiceAi`'s broker, you rarely call it yourself: the broker hangs a session up as
  soon as its handler returns or throws.

## Starting and stopping the server

The server binds one listener and runs one accept loop in its lifetime.

- A `StartAsync` while the server is running binds nothing, so a host that starts the same instance
  twice (as the hosted service `AddAudioSocketServer` registers, and again by hand) keeps one
  listener, and one stop releases everything.
- The server is not restartable: a `StartAsync` after `StopAsync` binds nothing. Create a new server
  instead.
- A `StartAsync` after `DisposeAsync` throws `ObjectDisposedException`; a `StopAsync` after it does
  nothing.
- The stop closes the sessions it serves without a hangup frame. Register the server before the
  Voice AI pipeline or the Realtime bridge, so their broker stops first and ends each call with one.
- Once the stop has begun, nothing more is registered: a connection that identified itself just
  before is refused with a hangup frame and never announced.

## The session limit

`MaxConcurrentSessions` is never passed. Each channel id the server serves takes its place after the
connection has identified itself, in the same atomic step that checks the limit, so a burst of calls
that identify at once admits exactly the limit and refuses the rest. Each refused call receives a
hangup frame and the server logs `Session limit reached` at Warning. A call that comes back with the
UUID of a session still ending shares that session's place (below). Size the limit for the peak you
want served: a deployment that ran past it in bursts on 2.6.1 and earlier now refuses those calls.

## A call that comes back with the same UUID

Asterisk does not keep the AudioSocket UUID unique. A dialplan that runs `AudioSocket()` or
`Dial(AudioSocket/…)` again with the UUID it saved, a transfer, or a redirect that re-enters the bot
connects again with the same UUID, often before the previous session has finished ending.

- **The call is served.** The new connection waits for the previous session with that UUID to end,
  for at most 1 second, and is then served. `OnSessionStarted` is raised for it only after every
  `OnHangup` handler of the previous session has returned, so state you key by `ChannelId` never
  holds two live sessions under one UUID. Keep `OnHangup` handlers short: while they run, the
  session still holds its UUID and counts in `ActiveSessionCount`, and a handler that takes longer
  than the wait makes the call that comes back be refused.
- **A UUID still live after 1 second is refused.** Two concurrent calls configured with one UUID are
  a dialplan error: the second connection receives a hangup frame, is closed, and the server logs the
  `ChannelIdInUse` Warning naming the UUID and how long it waited. The first call is untouched.
- **What the caller hears after a refusal.** Every refusal, for a UUID in use or for
  `MaxConcurrentSessions`, writes a hangup frame before closing. On Asterisk 20 and later,
  `AudioSocket()` then returns and the dialplan goes on. On Asterisk 18 any end from the server,
  a hangup frame included, fails the application and the call is hung up.

Give every concurrent AudioSocket call its own UUID, for example `Set(BOTID=${UUID()})` once per
call, and reuse it only when the same call comes back to the bot.

## Documentation

See the [main README](../../README.md) for full documentation. Upgrading from 2.6.1: [voice-session-ending-migration.md](../../docs/guides/voice-session-ending-migration.md).

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
- `AudioSocketSession` — per-call session with `ReadAudioAsync()` and `WriteAudioAsync()` for 20 ms PCM16 frames
- UUID handshake: automatically reads the channel UUID frame from Asterisk on connection
- `OnSessionStarted` event for routing sessions to custom handlers
- `AddAudioSocketServer()` DI extension for one-line registration
- Zero-copy `System.IO.Pipelines` framing; Native AOT compatible

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

See the [main README](../../README.md) for full documentation.

# Asterisk.Sdk.VoiceAi.AudioSocket

AudioSocket transport for Asterisk.Sdk.VoiceAi — bridges Asterisk AudioSocket streams into the Voice AI pipeline.

## Installation

```bash
dotnet add package Asterisk.Sdk.VoiceAi.AudioSocket
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

## Documentation

See the [main README](../../README.md) for full documentation.

# FastAgiServerExample

Demonstrates how to start a FastAGI server, register a script handler, and process incoming calls from the Asterisk dialplan.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AGI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/FastAgiServerExample/
```

Configure the Asterisk dialplan to point at the server:

```
exten => 100,1,AGI(agi://localhost/hello)
```

## What It Shows

- Creating a `FastAgiServer` on port 4573
- Registering an `IAgiScript` with `SimpleMappingStrategy`
- Answering a call, playing a sound file, and hanging up inside a script
- Graceful start/stop of the AGI server

## Key SDK Packages Used

- `Asterisk.Sdk.Agi` — FastAGI server, `IAgiScript`, `IAgiChannel`

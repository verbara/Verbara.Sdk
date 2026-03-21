# AgiIvrExample

Demonstrates a multi-menu IVR built with FastAGI: DTMF collection, conditional routing to queues or voicemail, and channel variable management.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AGI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/AgiIvrExample/
```

Configure the Asterisk dialplan:

```
exten => 100,1,AGI(agi://localhost:4573/ivr-main)
exten => 101,1,AGI(agi://localhost:4573/ivr-support)
exten => 102,1,AGI(agi://localhost:4573/ivr-sales)
```

## What It Shows

- `GetDataAsync` to collect DTMF digits with a configurable timeout
- Conditional call routing: press 1 for Support queue, 2 for Sales, `*` to repeat
- Fallback to voicemail after three failed attempts
- `SetVariableAsync` / `GetVariableAsync` for channel variable management
- Multiple named scripts registered on one `FastAgiServer`

## Key SDK Packages Used

- `Asterisk.Sdk.Agi` — FastAGI server, `IAgiChannel`, `SimpleMappingStrategy`

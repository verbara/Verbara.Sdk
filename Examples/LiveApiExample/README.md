# LiveApiExample

Demonstrates real-time tracking of active channels, queues, and agents by subscribing to AMI events through the Live API layer.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AMI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/LiveApiExample/
```

## What It Shows

- Starting `AsteriskServer` to load initial PBX state and subscribe to events
- Reading live counters: `ChannelCount`, `QueueCount`, `AgentCount`
- Polling updated counts on a 5-second interval
- Proper disposal of the live server before disconnecting from AMI

## Key SDK Packages Used

- `Asterisk.Sdk.Ami` — AMI connection
- `Asterisk.Sdk.Live` — `AsteriskServer`, `ChannelManager`, `QueueManager`, `AgentManager`
- `Asterisk.Sdk.Hosting` — DI registration (`AddAsterisk`)

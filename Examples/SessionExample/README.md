# SessionExample

Demonstrates real-time session monitoring: subscribing to call lifecycle domain events and printing periodic summaries of active and recently completed sessions.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AMI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/SessionExample/
```

## What It Shows

- Registering sessions with `AddAsteriskSessions()` alongside `AddAsterisk()`
- Subscribing to `ICallSessionManager.Events` to receive typed domain events
- Handling `CallStartedEvent`, `CallConnectedEvent`, `CallHeldEvent`, `CallResumedEvent`, `CallEndedEvent`, and `CallFailedEvent`
- Displaying wait time, talk time, agent ID, and queue name from event data
- Printing a live summary every 10 seconds: active session count and recently completed count

## Key SDK Packages Used

- `Asterisk.Sdk.Sessions` — `ICallSessionManager`, session domain events
- `Asterisk.Sdk.Hosting` — `AddAsterisk()`, `AddAsteriskSessions()`

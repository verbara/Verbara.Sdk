# PbxActivitiesExample

Demonstrates high-level PBX Activities on an incoming AGI call: play a message, dial an agent, fall back to a queue if unanswered, and hang up — each step tracked with a lifecycle `Status`.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AGI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/PbxActivitiesExample/
```

Configure the Asterisk dialplan:

```
exten => 200,1,AGI(agi://localhost:4573/activities-demo)
```

## What It Shows

- `PlayMessageActivity` to play a welcome prompt
- `DialActivity` to reach an agent with a timeout, including `DialStatus` inspection
- `QueueActivity` as fallback when the direct dial does not answer
- `HangupActivity` for clean call termination
- Each activity reports its `Status` through the full lifecycle

## Key SDK Packages Used

- `Asterisk.Sdk.Activities` — `PlayMessageActivity`, `DialActivity`, `QueueActivity`, `HangupActivity`
- `Asterisk.Sdk.Agi` — FastAGI server and `IAgiChannel`

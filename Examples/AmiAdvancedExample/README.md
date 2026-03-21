# AmiAdvancedExample

Demonstrates multiple AMI actions, event filtering by type, and event-generating actions that stream results asynchronously.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AMI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/AmiAdvancedExample/
```

## What It Shows

- Filtering events by type using a custom `IObserver<ManagerEvent>` with a `HashSet` allowlist
- Sending `CommandAction` to execute a CLI command (e.g. `core show uptime`)
- `QueueAddAction` / `QueueRemoveAction` to manage queue membership
- `OriginateAction` to place an outbound call
- `SendEventGeneratingActionAsync` with `StatusAction` to enumerate active channels as an async stream
- Action/response correlation via `ActionId`

## Key SDK Packages Used

- `Asterisk.Sdk.Ami` — AMI connection, actions, and events
- `Asterisk.Sdk.Hosting` — DI registration (`AddAsterisk`)

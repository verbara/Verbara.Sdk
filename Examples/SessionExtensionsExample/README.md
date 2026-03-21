# SessionExtensionsExample

Demonstrates implementing a custom `SessionStoreBase` that persists call session snapshots as JSON lines to a local file, and registering it before the built-in sessions layer.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AMI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/SessionExtensionsExample/
```

Session data is written to `sessions.jsonl` in the working directory.

## What It Shows

- Subclassing `SessionStoreBase` to create a `FileSessionStore` that appends JSON lines
- Registering a custom store with `AddSingleton<SessionStoreBase>` before `AddAsteriskSessions()`
- Observing the same typed domain events as `SessionExample`
- Calling `fileStore.PrintSummary()` on shutdown to report all persisted sessions

## Key SDK Packages Used

- `Asterisk.Sdk.Sessions` — `SessionStoreBase`, `ICallSessionManager`, session events
- `Asterisk.Sdk.Sessions.Extensions` — custom store extension point
- `Asterisk.Sdk.Hosting` — `AddAsterisk()`, `AddAsteriskSessions()`

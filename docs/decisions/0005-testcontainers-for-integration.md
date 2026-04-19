# ADR-0005: Testcontainers is the integration-test substrate

- **Status:** Accepted
- **Date:** 2026-04-16 (retrospective)
- **Deciders:** Harol A. Reina H.

## Context

The SDK talks to live Asterisk via AMI (TCP), ARI (WebSocket), AGI (TCP), and Realtime (Postgres). Integration tests need a running Asterisk 22 with manager.so, ARI HTTP, and realtime-ready config. We have three classes of tests:

- **Unit tests** (`Tests/Asterisk.Sdk.*.Tests/`): pure in-process, no network — 2,643 tests, runs on any machine in under 60 s.
- **Integration tests** (`Tests/Asterisk.Sdk.IntegrationTests/`, `Asterisk.Sdk.Sessions.{Redis,Postgres}.Tests/`): exercise Redis/Postgres/Toxiproxy with real services. No Asterisk.
- **Functional tests** (`Tests/Asterisk.Sdk.FunctionalTests/`): **full stack** — Asterisk container + Postgres realtime + SIPp + Toxiproxy + custom PSTN emulator.

The options we evaluated:

1. **Docker Compose + CI-provided services.** Requires a `docker-compose.test.yml` that developers start before `dotnet test`, and Actions runners need matching service definitions.
2. **Testcontainers for .NET.** Programmatic container lifecycle; each test fixture owns its Docker stack; containers are named after the fixture and auto-cleaned by Ryuk.
3. **Mock AMI/ARI servers in-process.** Faster, but diverges from Asterisk's real behavior (we've been burned by this with asterisk-java).

## Decision

**Testcontainers for .NET** is the integration-test substrate for anything that needs a service. Each test fixture owns its stack; a dedicated `Asterisk.Sdk.TestInfrastructure` project wraps the five containers we use (Asterisk, Postgres, PstnEmulator, Sipp, Toxiproxy).

Specific rules:

- The Asterisk image builds from our own `docker/Dockerfile.asterisk` (Asterisk 22 + our test config). Image build is cached locally + pre-pulled in CI.
- Wait strategies use `UntilCommandIsCompleted` (e.g. `pg_isready`, `asterisk -rx "core show uptime"`) or `UntilInternalTcpPortIsAvailable` — never `/proc/net/tcp`-based polling because that is empty on GitHub Actions runners and hangs for 30 min.
- The `docker/docker-compose.test.yml` file exists as a **convenience** for developers who want to inspect the stack manually — it is not the test substrate.

## Consequences

- **Positive:** Tests are self-contained. A contributor with Docker and .NET 10 can run `dotnet test --filter Category=Functional` without reading any README. CI matches local exactly. 148 functional tests + 65 integration tests run in 15–16 min on a Ryzen 9 9900X.
- **Negative:** Testcontainers updates can be breaking — `4.4+` removed `new ContainerBuilder()` parameterless ctor and `.UntilPortIsAvailable(port)`, forcing a migration (see PR #20). Docker daemon is a hard requirement; the `Asterisk.Sdk.Sessions.Redis.Tests` and `Asterisk.Sdk.Sessions.Postgres.Tests` projects are marked `[Trait("Category", "Integration")]` so the default unit filter skips them.
- **Trade-off:** We accept the ~15-min CI ceiling on functional tests for the confidence that "if it passes here, it passes in prod."

## Alternatives considered

- **Docker Compose + bash orchestration** — rejected because fixture lifecycle leaks across test runs; Ryuk (Testcontainers' reaper) is more reliable than `docker compose down`.
- **Mock AMI/ARI servers** — rejected because the SDK's reconnection tests (socket close + re-auth + event replay) need real network semantics; mocks can't fake TCP RST or socket half-close.
- **Skip integration tests, rely on unit + manual QA** — rejected because Asterisk has many quirks (e.g. `announce_user_count=yes` blocking ConfBridge 3rd join in CI) that only surface with a live daemon.

## Notes

- Image pins as of PR #20: `postgres:18-alpine`, `ghcr.io/shopify/toxiproxy:2.12.0`, `redis:7-alpine`, `andrius/asterisk:22` (base for Dockerfile.asterisk).
- Redis is deliberately kept on 7.x despite Redis 8 being available: Redis 8 switched to SSPLv1, incompatible with the SDK's MIT licensing.

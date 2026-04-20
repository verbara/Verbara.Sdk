# Asterisk version support matrix

The SDK targets **Asterisk 22 LTS** as its primary support tier and adds **Asterisk 23 Standard** as a secondary tier starting with SDK v1.15.0. This guide describes which versions are covered, the Docker test infrastructure for each, and known-divergent behaviour to watch for.

## Supported versions (as of SDK v1.15.0 — 2026-04-20)

| Asterisk | Type | Released | Security until | EOL | SDK support | CI coverage |
|---|---|---|---|---|---|---|
| **22.x** | LTS | 2024-10-16 | 2028-10-16 | 2029-10-16 | **Primary** — production recommended | Full Functional + Integration suites |
| **23.x** | Standard | 2025-10-15 | 2026-10-15 | 2027-10-15 | **Secondary** — opt-in dual matrix | Dual CI matrix (this release); functional subset |
| 24.x (future) | LTS | Expected 2026-10 | TBD | TBD | Planned after GA | Matrix addition in the first post-24-GA release |
| 20.x | LTS (old) | 2022-10-19 | 2026-10-19 | 2027-10-19 | Not supported (out of CI) | — |

Source of truth for Asterisk lifecycle: https://docs.asterisk.org/About-the-Project/Asterisk-Versions/

## Running the 22 LTS stack

```sh
docker compose -f docker/docker-compose.test.yml up --build --abort-on-container-exit
```

Container names use the `asterisk-sdk-test` prefix; host ports are `15038` (AMI) and `15088` (ARI HTTP) — baseline for Functional and Integration test suites.

## Running the 23 Standard stack

```sh
docker compose -f docker/docker-compose.test-23.yml up --build --abort-on-container-exit
```

Parallel stack — can coexist with the 22 stack. Container names use the `asterisk-sdk-test-23` prefix; host ports are `15040` (AMI) and `15090` (ARI HTTP) so tests can hit either deployment without rebinding.

## Build-arg driven image

`docker/Dockerfile.asterisk` accepts two build-args to select the Asterisk line:

```sh
docker build -f docker/Dockerfile.asterisk \
  --build-arg ASTERISK_VERSION=23 \
  --build-arg CODEC_OPUS_VERSION=23.0_1.3.0 \
  -t asterisk-sdk-test:23 .
```

`ASTERISK_VERSION` selects the base image tag (`andrius/asterisk:${ASTERISK_VERSION}`). `CODEC_OPUS_VERSION` picks the matching Digium Opus binary. If Digium has not yet published a codec_opus build for the selected version, the image continues to build with a warning — the SDK test suites do not exercise Opus audio payloads.

## Known break-change risk areas between 22 ↔ 23

| Area | 22 baseline | 23 delta (as of 2026-04-20 research) | SDK exposure |
|---|---|---|---|
| AMI action set | Stable | Upstream changelog notes no removed actions; several additive fields on `CoreShowChannels` / `Status` | **Low** — additive fields tolerated by generated parsers. |
| ARI schemas | REST+WS stable | Minor additions to `channel` / `bridge` snapshots (check `GetInfoAsync` for new fields) | **Low** — our Ari.Client treats unknown fields as tolerated. |
| PJSIP / Realtime columns | schema v22 | Upstream PJSIP additions; column set for `ps_endpoints` / `ps_aors` grew by ~3 fields | **Medium** — Pro.Realtime provisioning may need additive column support. Not blocking. |
| Dialplan applications | Stable | No removals | **None** for the SDK (we don't inject dialplan). |
| Codec negotiation | Opus optional | Opus optional | **None**. |

When a divergence is detected by running the 23 matrix, file it here with an explicit fix plan.

## CI matrix expectations

The CI workflow runs Functional + Integration tests against both stacks in parallel jobs. A failure isolated to the 23 matrix surfaces as a non-blocking flag on the PR unless the failure category is "regression in SDK public API" — in which case it promotes to a blocker.

## Migration guidance for consumers

- Default: stay on Asterisk 22 LTS until 24 LTS ships (October 2026). LTS offers security support through 2028.
- Early adopters on 23: pin the Docker stack via `docker-compose.test-23.yml` + run Functional matrix locally before upgrading production PBXs.
- Do not mix 22 and 23 nodes within the same cluster — Pro.Cluster assumes homogeneous versions for session snapshot compatibility.

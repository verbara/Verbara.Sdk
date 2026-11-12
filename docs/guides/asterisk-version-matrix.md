# Asterisk version support matrix

The SDK supports the **Asterisk LTS releases** as its primary tier and the current **Standard release** as a secondary
tier until that release's end of life. This guide states which versions are covered, what CI runs against each, the
Docker test infrastructure, and known-divergent behaviour to watch for.

## Support policy (SDK 2.10.0)

2.10.0's CI runs the Functional and Integration suites on Asterisk 22.12.0, 23.6.0 and 24.0.0, the generally available
builds upstream published on 2026-11-05, each pinned by digest in `docker/asterisk-base-images.txt`. 24.0.0 is the
first generally available 24 build, so 2.10.0 is the first release that supports Asterisk 24.

- **Primary — Asterisk 22 LTS and Asterisk 24 LTS.**
- **Secondary — Asterisk 23 Standard**, until its end of life on 2027-10-15. Upstream ships it security fixes only
  from 2026-10-15.
- **Not supported — Asterisk 20 and earlier.** 20 has been out of the SDK's CI since v1.15.0, although upstream
  ships it security fixes until 2027-10-19. 18 reached end of life on 2025-10-20. Some guides record measurements
  taken on 20, but no release is tested on it.

| Asterisk | Type | Released | Security fixes only from | EOL | SDK support | CI coverage |
|---|---|---|---|---|---|---|
| **22.x** | LTS | 2024-10-16 | 2028-10-16 | 2029-10-16 | **Primary** — production recommended | Functional + Integration suites in the merge queue (pinned build 22.12.0: `docker/asterisk-base-images.txt`) |
| **23.x** | Standard | 2025-10-15 | 2026-10-15 | 2027-10-15 | **Secondary** — until EOL | Functional + Integration suites on pinned build 23.6.0 in the merge queue (on a pull request only with the `ci:functional` label) |
| **24.x** | LTS | 2026-11-05 (24.0.0) | 2030-10-15 (planned) | 2031-10-15 (planned) | **Primary** — from SDK 2.10.0 | Functional + Integration suites on pinned build 24.0.0 in the merge queue |
| 20.x | LTS (old) | 2022-10-19 | 2026-10-19 | 2027-10-19 | Not supported | Not in CI (a build stays pinned for local measurement) |
| 18.x | LTS (old) | 2020-10-20 | 2024-10-20 | 2025-10-20 | Not supported | — |

Lifecycle dates: https://docs.asterisk.org/About-the-Project/Asterisk-Versions/ (read 2026-10-28); release dates of
22.12.0, 23.6.0 and 24.0.0: the `asterisk/asterisk` GitHub releases.

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

## Running the 24 LTS stack

```sh
docker compose -f docker/docker-compose.test-24.yml up --build --abort-on-container-exit
```

Parallel stack, like the 23 one. Container names use the `asterisk-sdk-test-24` prefix; host ports are `15042` (AMI)
and `15091` (ARI HTTP).

## Build-arg driven image

`docker/Dockerfile.asterisk` builds on a pinned base: `docker/asterisk-base-images.txt` holds one line per supported
version, `<version> <image>:<build tag>@<digest>` (22, 23 and 24 for CI, 20 for local measurement). A version tag such as
`22` is re-published with a new build every week; the per-build tag never moves and the digest names the exact image,
so every run of a version tests the same Asterisk. Testcontainers and the CI pre-build read the line for the
`ASTERISK_VERSION` they build, and a version with no line fails before any build.

A hand-run build passes the version, its Opus build and its base:

```sh
docker build -f docker/Dockerfile.asterisk docker/ \
  --build-arg ASTERISK_VERSION=23 \
  --build-arg CODEC_OPUS_VERSION=23.0_1.3.0 \
  --build-arg ASTERISK_BASE_IMAGE="$(awk '$1 == "23" { print $2 }' docker/asterisk-base-images.txt)" \
  -t asterisk-sdk-test:23
```

With no `ASTERISK_BASE_IMAGE` the base is the table's 22 line. The Dockerfile checks that the base's `asterisk -V` is
the selected `ASTERISK_VERSION` and fails otherwise ("base is Asterisk 22.x.y, not Asterisk 23"), so a build handed
only the version never produces the wrong server. Moving a version to a newer build is one edit to the table, with the
new build's `asterisk -V` and digest measured first. `CODEC_OPUS_VERSION` picks the matching Digium Opus binary. If Digium has not yet published a codec_opus build for the selected version, the image continues to build with a warning — the SDK test suites do not exercise Opus audio payloads.

## Running the suites against a prebuilt image

The Functional and Integration fixtures build `docker/Dockerfile.asterisk` through Testcontainers. Before that build,
Testcontainers pulls the Dockerfile's base image unless a local image carries the base's name as a *tag*: a base named
by a content id (`sha256:…`), or by the digest of an image that exists only on your machine, is never a tag, so the
pull runs, fails with `pull access denied`, and every fixture fails before Asterisk starts. To test a build of your
own, such as a release candidate you built locally, build the test image yourself and name it in
`ASTERISK_TEST_IMAGE`:

```sh
docker build -f docker/Dockerfile.asterisk docker/ \
  --build-arg ASTERISK_VERSION=22 \
  --build-arg CODEC_OPUS_VERSION=22.0_1.3.0 \
  --build-arg ASTERISK_BASE_IMAGE=<your base: name:tag or name@sha256:digest> \
  -t asterisk-sdk-test:local
ASTERISK_VERSION=22 ASTERISK_TEST_IMAGE=asterisk-sdk-test:local \
  dotnet test Tests/Verbara.Sdk.FunctionalTests/ --filter "Category=Functional"
```

`docker build` with BuildKit resolves a bare `sha256:…` id as a registry name and fails, so name the base by a tag
or `name@sha256:…`; the variable itself takes any of the three. With the variable set, the fixtures build nothing:
they look the image up on the Docker host Testcontainers uses (by tag, digest or id), start it by its content id, and
never pull it. A name the host does not have fails the first fixture with a message naming the variable. Unset or
blank, the fixtures build from the pinned base exactly as above.

The image decides which Asterisk runs, so set `ASTERISK_VERSION` to its major version; a test that compares the
server's version with the pinned line of `docker/asterisk-base-images.txt` reports the difference, because it measures
your build, not the pin. CI never sets the variable: the Governance suite fails if a workflow names it, so the
merge-queue legs always build on the table's pins.

## Known break-change risk areas between 22 ↔ 23

| Area | 22 baseline | 23 delta (as of 2026-04-20 research) | SDK exposure |
|---|---|---|---|
| AMI action set | Stable | Upstream changelog notes no removed actions; several additive fields on `CoreShowChannels` / `Status` | **Low** — additive fields tolerated by generated parsers. |
| ARI schemas | REST+WS stable | Minor additions to `channel` / `bridge` snapshots (check `GetInfoAsync` for new fields) | **Low** — our Ari.Client treats unknown fields as tolerated. |
| PJSIP / Realtime columns | schema v22 | Upstream PJSIP additions; column set for `ps_endpoints` / `ps_aors` grew by ~3 fields | **None** — the SDK does not read or write the Realtime tables. |
| Dialplan applications | Stable | No removals | **None** for the SDK (we don't inject dialplan). |
| Codec negotiation | Opus optional | Opus optional | **None**. |

When a divergence is detected by running the 23 matrix, file it here with an explicit fix plan.

## CI matrix expectations

The `Functional Tests (Testcontainers)` job of `.github/workflows/ci.yml` runs the Functional and Integration suites
against 22, 23 and 24 in the merge queue; on a pull request they run only when it carries the `ci:functional` label.
The 23 leg is a required check, so a 23 failure in the queue blocks the merge; the 22 and 24 legs are not required
checks, so a failure only on 22 or 24 has to be read from the queue run. Each leg builds its image on the build that
`docker/asterisk-base-images.txt` pins for that version.

## Migration guidance for consumers

- Default: run an LTS, Asterisk 22 or 24. Both are primary from SDK 2.10.0.
- On 23: plan the move to an LTS before 23's end of life on 2027-10-15. To try a 23 build locally, start the stack
  from `docker-compose.test-23.yml` and run the Functional suite against it before upgrading production PBXs.
- On 20 or earlier: upgrade to 22. The SDK is not tested on 20, and 2.9.0 removed 26 legacy event types; what an
  Asterisk that still sends one of them delivers is in [the compatibility guide](asterisk-version-compatibility.md).

# Contributing to Verbara.Sdk

Thank you for your interest in contributing to Verbara.Sdk! This document provides guidelines and instructions for contributing.

## Getting Started

### Prerequisites

- [.NET 10.0.100+](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- Docker (for running Asterisk containers in integration/functional tests)
- An Asterisk 18-23 instance with AMI enabled (for manual testing)

### Setup

```bash
git clone https://github.com/verbara/Verbara.Sdk.git
cd Verbara.Sdk
dotnet build Verbara.Sdk.slnx
dotnet test Verbara.Sdk.slnx

# One-time: install the pre-commit hook that lints CLAUDE.md + .claude/
# on every commit using claudelint. Install claudelint first:
#   npm install -g claude-code-lint
./tools/install-hooks.sh
```

### Project Structure

```
src/                    # 28 SDK packages: 9 core + 8 VoiceAi + 4 Push + 2 Sessions backends
                        # + 2 Cluster (Primitives + Postgres) + 1 Data.Npgsql + 1 Resilience
                        # + 1 OpenTelemetry meta + 1 SourceGenerators analyzer
Examples/               # 26 example applications (BasicAmi, FastAGI, ARI, Sessions, VoiceAi, ...)
Tests/                  # 33 test projects — Unit, Functional, Integration (Testcontainers)
docker/                 # Docker Compose stacks for development and testing
docs/                   # Architecture (specs + decisions + research) and guides
```

## Development Workflow

### Branch Naming

- `feat/description` — New features
- `fix/description` — Bug fixes
- `docs/description` — Documentation only
- `test/description` — Test additions or fixes
- `refactor/description` — Code refactoring

### Commit Messages

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(ami): add PJSIPShowRegistrationsAction
fix(live): fix race condition in ChannelManager state update
docs: update high-load tuning guide
test: add functional tests for queue events
refactor(agi): extract command parser into separate class
```

Scope is optional but recommended. Common scopes: `ami`, `agi`, `ari`, `live`, `sessions`, `push`, `hosting`, `voiceai`, `activities`, `config`, `audio`, `docker`.

### Code Conventions

- **AOT constraint:** No reflection at runtime. Use source generators, `[JsonSerializable]`, `[OptionsValidator]`.
- **Async-first:** All I/O uses `ValueTask`/`Task` with `CancellationToken` support.
- **Private fields:** `_camelCase` prefix.
- **File-scoped namespaces** (warning-level enforcement).
- **TreatWarningsAsErrors** is on globally — build must be 0 warnings.
- **Test naming:** `Method_ShouldExpected_WhenCondition`.
- **Test stack:** xUnit, FluentAssertions and NSubstitute. Their versions are pinned in `Directory.Packages.props`; read them there, because a copy here goes stale.
- **Central package management:** All NuGet versions in `Directory.Packages.props`.

### Build & Test

```bash
# Build entire solution
dotnet build Verbara.Sdk.slnx

# Run the unit lane (the filter CI's Unit Tests job uses)
dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"

# Run a specific test project
dotnet test Tests/Verbara.Sdk.Ami.Tests/

# Run a single test by name
dotnet test Tests/Verbara.Sdk.Ami.Tests/ --filter "FullyQualifiedName~AmiProtocolReaderTests"

# Run functional + integration tests (requires Docker; Testcontainers starts the containers)
dotnet test Verbara.Sdk.slnx --filter "Category=Functional|Category=Integration|Category=Realtime" -- RunConfiguration.MaxCpuCount=1
```

## Pull Request Process

1. **Fork and branch** from `main`.
2. **Write tests first** — follow TDD. New features need unit tests. Bug fixes need a regression test.
3. **Build must pass** with 0 warnings (`TreatWarningsAsErrors` is on).
4. **All existing tests must pass.**
5. **Keep PRs focused** — one feature or fix per PR. Don't mix refactoring with features.
6. **Update CHANGELOG.md** if your change is user-facing.
7. **Submit PR** with a clear description of what and why.

### PR Review Criteria

- Does it build with 0 warnings?
- Are there tests? Do they pass?
- Does it follow the code conventions above?
- Is it AOT-safe (no runtime reflection)?
- Is the commit message conventional?

## Release Process (Maintainers)

Releases are driven by tag pushes matching `v*` (e.g. `v1.12.0`, `v1.12.1`). The `.github/workflows/publish.yml` workflow checks that the tagged commit was built green on `main`, builds in Release, packs every shipping project, builds the release notes from `CHANGELOG.md`, pushes all `.nupkg` files to nuget.org via `dotnet nuget push --skip-duplicate`, and creates the GitHub Release. No manual `dotnet nuget push` or `gh release create` is needed.

### One-time setup — `NUGET_API_KEY` secret

The workflow reads `${{ secrets.NUGET_API_KEY }}` from GitHub repository secrets. If the secret is missing or expired, the push step fails with HTTP 403 on the first package. Set or rotate the key **without pasting it into chat, commits, or issue comments** — any secret that travels through a transcript should be rotated immediately after.

The safe flow is local-only — the value never appears in the command line or shell history:

```bash
# 1. Generate / rotate at https://www.nuget.org/account/apikeys
#    Scopes: "Push new packages and package versions"
#    Glob Pattern: Verbara.Sdk.*
#    Expiration: 365 days (max)

# 2. Pipe from clipboard or password manager directly to gh secret set:
pbpaste | gh secret set NUGET_API_KEY --repo verbara/Verbara.Sdk              # macOS
xclip -selection clipboard -o | gh secret set NUGET_API_KEY --repo verbara/Verbara.Sdk   # Linux X11
wl-paste | gh secret set NUGET_API_KEY --repo verbara/Verbara.Sdk              # Linux Wayland
pass show nuget/api-key | gh secret set NUGET_API_KEY --repo verbara/Verbara.Sdk         # pass(1)

# 3. Verify the secret is registered (value stays encrypted):
gh secret list --repo verbara/Verbara.Sdk
```

### Cutting a release

A release is two pull requests and one tag push. `main` takes changes only through the merge queue, so neither the version bump nor the baseline move is committed to it directly.

```bash
# 1. Read the current state from the tree, not from memory.
git switch main && git pull --ff-only && git fetch --tags
grep -o '<PackageVersion>[^<]*' Directory.Build.props
git tag --list 'v*' --sort=-v:refname | head -3

# 2. Release PR, titled `chore(release): X.Y.Z`. It touches four files:
#    - Directory.Build.props   <PackageVersion> → X.Y.Z
#    - CHANGELOG.md            rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`
#                              and open a new, empty `## [Unreleased]` above it
#    - README.md               the Status block headline → **vX.Y.Z**
#                              (StatusBlockCoherenceTests fails while it disagrees with <PackageVersion>)
#    - docs/claim-registry.md  the headline-version row
#    A section with a `### Changed — BREAKING` heading ships as a minor. A section whose
#    breaking entries are all `### Fixed — BREAKING` may ship as a patch.

# 3. Optional readiness check, after the release PR is merged: run publish.yml on main by
#    workflow_dispatch. It is a dry run — it packs, verifies the version and builds the notes
#    from the CHANGELOG section, and publishes nothing. A dry run that FAILS leaves a failed
#    check run on the release commit, and the tag's provenance check refuses that commit until
#    the run is re-run green.
gh workflow run publish.yml --ref main

# 4. Tag the merged release commit and push the tag. This fires publish.yml.
#    The `release-tags` ruleset restricts creating, updating and deleting v* tags
#    to organisation admins.
git tag -a vX.Y.Z -m "vX.Y.Z" <release-commit-sha>
git push origin vX.Y.Z
gh run watch --exit-status

# 5. publish.yml polls the feed for ONE package. Confirm all of them:
for d in src/*/; do
  p="$(basename "$d" | tr '[:upper:]' '[:lower:]')"
  curl -fsS "https://api.nuget.org/v3-flatcontainer/$p/index.json" | grep -q '"X.Y.Z"' || echo "not on the feed yet: $p"
done

# 6. Baseline PR, titled `chore(release): move the package validation baseline to X.Y.Z`,
#    opened only after the tag exists:
#    - Directory.Build.props   <PackageValidationBaselineVersion> → X.Y.Z
#    - delete every src/**/CompatibilitySuppressions.xml
```

Between the two PRs the **Release Hygiene** workflow reports two expected failures: after the release PR merges, *Publish Liveness* says the version is "staged but never tagged" until the tag is pushed; after the tag is pushed, *ApiCompat Baseline* says the baseline is behind the newest tag until the baseline PR merges (`scripts/ci/check-publish-liveness.sh`, `scripts/ci/check-apicompat-baseline.sh`). Neither blocks a merge or a publish.

If `publish.yml` refuses or fails, **do not delete and re-push the tag** — the `release-tags` ruleset blocks it for everyone except organisation admins, and no failure needs it:

- **The provenance check refused the tag** (a completed check run on the tagged commit did not conclude success, skipped or neutral — `scripts/ci/check-release-provenance.sh`). Re-run the failed check, then re-run the publish run:

  ```bash
  gh run rerun <failed-run-id> --failed
  gh run rerun <publish-run-id>
  ```

- **The push stopped partway through.** Fix the cause and re-run the publish run. `--skip-duplicate` skips the packages already on the feed, and the run then checks the feed and creates the GitHub Release if it does not exist yet.

## Reporting Issues

- Use [GitHub Issues](https://github.com/verbara/Verbara.Sdk/issues) for bugs and feature requests.
- For security vulnerabilities, see [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).

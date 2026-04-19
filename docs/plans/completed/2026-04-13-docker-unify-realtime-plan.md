# Docker Infrastructure Unification (Realtime-only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unify all Asterisk test containers to a single `Dockerfile.asterisk` image. Run the main Asterisk in Realtime mode (PostgreSQL-backed PJSIP) and the PSTN emulator in file-based mode (same image, different config mount). Restore missing PSTN config, restore working functional+integration tests, and remove the `continue-on-error: true` workaround in CI.

**Architecture:**
- ONE Dockerfile `docker/Dockerfile.asterisk` based on `andrius/asterisk:22` (Debian) — bundles codec_opus, English + Spanish sounds, and a startup entrypoint. Built once by Testcontainers.
- TWO config directories: `docker/functional/asterisk-config/` (realtime, unified merge of current `asterisk-realtime-config/` + `test-config/`) and `docker/functional/pstn-emulator-config/` (file-based, restored from git).
- `AsteriskContainer` runs realtime mode and depends on `PostgresContainer`. `PstnEmulatorContainer` uses the same image with the file-based config.
- Integration tests and Functional tests both now require Postgres — fixtures updated.
- Work lands on a feature branch `chore/docker-unify-realtime`, PR to `main`.

**Tech Stack:** Docker, Testcontainers .NET, Asterisk 22 (Debian), PostgreSQL 17, xunit 2.9.3.

---

## File Structure

**Create:**
- `docker/Dockerfile.asterisk` — unified image (codec_opus + sounds + entrypoint)
- `docker/entrypoint-asterisk.sh` — startup script
- `docker/functional/asterisk-config/` — unified realtime+ARI config directory (16 files)
- `docker/functional/pstn-emulator-config/` — restored from git (5 files)
- `.github/pull_request_template.md` — optional PR template (skip if exists)

**Modify:**
- `docker/docker-compose.test.yml` — use `build:` not `image:`, single Dockerfile
- `Tests/Asterisk.Sdk.TestInfrastructure/DockerPaths.cs` — drop file-based paths, add unified ones
- `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PostgresContainer.cs` — add `WithNetworkAliases("postgres")` so realtime config DNS resolves
- `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs` — convert to realtime (requires Postgres network), drop file-based bindmount
- `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskRealtimeContainer.cs` — DELETE (replaced by unified AsteriskContainer)
- `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PstnEmulatorContainer.cs` — point at new Dockerfile
- `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/IntegrationFixture.cs` — add PostgresContainer + network
- `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/FunctionalFixture.cs` — add PostgresContainer, start order (Postgres → Asterisk → PstnEmulator/Toxiproxy → SIPp)
- `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/RealtimeFixture.cs` — align with refactored AsteriskContainer
- `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/RealtimeFixture.cs` — update to new type
- `.github/workflows/ci.yml` — remove `continue-on-error: true` from functional-tests job

**Delete:**
- `docker/Dockerfile.asterisk-realtime` (replaced by `Dockerfile.asterisk`)
- `docker/test-config/` (file-based config, no longer used — merged into `asterisk-config/`)

---

## Task 1: Create feature branch via worktree

**Files:** none (git operations only)

- [ ] **Step 1: Launch brainstorming/using-git-worktrees skill**

Invoke `superpowers:using-git-worktrees` skill to create a worktree for branch `chore/docker-unify-realtime` off `main`. The skill handles safety checks.

If skill unavailable, fallback:

```bash
git worktree add -b chore/docker-unify-realtime ../Asterisk.Sdk-docker-unify main
cd ../Asterisk.Sdk-docker-unify
```

- [ ] **Step 2: Verify clean state**

Run: `git status`
Expected: `nothing to commit, working tree clean` on branch `chore/docker-unify-realtime`.

- [ ] **Step 3: Baseline build**

Run: `dotnet build Asterisk.Sdk.slnx -c Release`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s)`.

---

## Task 2: Create unified Dockerfile and entrypoint

**Files:**
- Create: `docker/Dockerfile.asterisk`
- Create: `docker/entrypoint-asterisk.sh`

- [ ] **Step 1: Write `docker/entrypoint-asterisk.sh`**

The fixtures start Postgres before Asterisk, so no DB-wait is needed in the entrypoint. This stays POSIX-sh compatible (no bashisms).

```sh
#!/bin/sh
set -e
exec /usr/sbin/asterisk -f
```

- [ ] **Step 2: Write `docker/Dockerfile.asterisk`**

```dockerfile
FROM andrius/asterisk:22

USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libopus0 \
    && rm -rf /var/lib/apt/lists/* \
    && cd /tmp \
    && curl -fsSL https://downloads.digium.com/pub/telephony/codec_opus/asterisk-22.0/x86-64/codec_opus-22.0_1.3.0-x86_64.tar.gz -o codec_opus.tar.gz \
    && tar xzf codec_opus.tar.gz \
    && cp codec_opus-22.0_1.3.0-x86_64/codec_opus.so /usr/lib/asterisk/modules/ \
    && cp codec_opus-22.0_1.3.0-x86_64/format_ogg_opus.so /usr/lib/asterisk/modules/ \
    && rm -rf codec_opus.tar.gz codec_opus-22.0_1.3.0-x86_64 \
    && mkdir -p /var/lib/asterisk/sounds/en \
    && cd /var/lib/asterisk/sounds/en \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-en-ulaw-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-en-gsm-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-en-sln16-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-extra-sounds-en-ulaw-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-extra-sounds-en-gsm-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-extra-sounds-en-sln16-current.tar.gz | tar xz \
    && mkdir -p /var/lib/asterisk/sounds/es \
    && cd /var/lib/asterisk/sounds/es \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-ulaw-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-gsm-current.tar.gz | tar xz \
    && curl -fsSL https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-es-sln16-current.tar.gz | tar xz \
    && chown -R asterisk:asterisk /var/lib/asterisk/sounds

COPY entrypoint-asterisk.sh /entrypoint-asterisk.sh
RUN chmod +x /entrypoint-asterisk.sh

USER asterisk
ENTRYPOINT ["/entrypoint-asterisk.sh"]
```

- [ ] **Step 3: Build image locally to verify**

Run: `cd docker && docker build -t asterisk-sdk-test:latest -f Dockerfile.asterisk . && cd ..`
Expected: successful build ending with `Successfully tagged asterisk-sdk-test:latest`. Takes ~5-10 min (sounds download).

- [ ] **Step 4: Smoke test the image**

Run: `docker run --rm -d --name asterisk-smoke asterisk-sdk-test:latest && sleep 8 && docker exec asterisk-smoke asterisk -rx "core show version" && docker stop asterisk-smoke`
Expected: shows `Asterisk 22.x.x`.

- [ ] **Step 5: Commit**

```bash
git add docker/Dockerfile.asterisk docker/entrypoint-asterisk.sh
git commit -m "feat(docker): add unified Asterisk 22 image with codec_opus and sounds"
```

---

## Task 3: Restore PSTN emulator config from git

**Files:**
- Create: `docker/functional/pstn-emulator-config/manager.conf`
- Create: `docker/functional/pstn-emulator-config/extensions.conf`
- Create: `docker/functional/pstn-emulator-config/pjsip.conf`
- Create: `docker/functional/pstn-emulator-config/modules.conf`
- Create: `docker/functional/pstn-emulator-config/voicemail.conf`

- [ ] **Step 1: Restore all five config files from git**

```bash
mkdir -p docker/functional/pstn-emulator-config
for f in manager.conf extensions.conf pjsip.conf modules.conf voicemail.conf; do
  git show 33a7dae^:docker/functional/pstn-emulator-config/$f > docker/functional/pstn-emulator-config/$f
done
```

- [ ] **Step 2: Verify all five files present and non-empty**

Run: `ls -la docker/functional/pstn-emulator-config/ && wc -l docker/functional/pstn-emulator-config/*.conf`
Expected: 5 files, each > 5 lines. `manager.conf` starts with `[general]`, `extensions.conf` contains `[from-dut]`, `pjsip.conf` contains `[transport-udp]`.

- [ ] **Step 3: Commit**

```bash
git add docker/functional/pstn-emulator-config/
git commit -m "feat(docker): restore pstn-emulator file-based config"
```

---

## Task 4: Unify realtime + ARI + test configs into single directory

**Files:**
- Create: `docker/functional/asterisk-config/` (16 files — see below)
- Keep `docker/functional/asterisk-realtime-config/` temporarily for diff reference, delete at end of task.

The new `asterisk-config/` starts from the existing `asterisk-realtime-config/` and adds ARI + richer dialplan + unified manager.conf credentials.

- [ ] **Step 1: Copy realtime config as starting point**

```bash
cp -r docker/functional/asterisk-realtime-config docker/functional/asterisk-config
```

- [ ] **Step 2: Replace `docker/functional/asterisk-config/manager.conf` with unified credentials**

```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
port = 5038

; Integration test user (Asterisk.Sdk.IntegrationTests AsteriskFixture default)
[testadmin]
secret = testpass
read = system,call,agent,user,config,originate,reporting,command,dtmf,cdr
write = system,call,agent,user,config,originate,command,reporting,dtmf
deny = 0.0.0.0/0.0.0.0
permit = 0.0.0.0/0.0.0.0

; Realtime-DB test user (Asterisk.Sdk.FunctionalTests RealtimeDbFixture default)
[dashboard]
secret = dashboard
read = system,call,agent,user,config,originate,reporting,command,dtmf,cdr
write = system,call,agent,user,config,originate,command,reporting,dtmf
deny = 0.0.0.0/0.0.0.0
permit = 0.0.0.0/0.0.0.0
```

- [ ] **Step 3: Replace `docker/functional/asterisk-config/http.conf` to enable ARI**

```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
bindport = 8088
```

- [ ] **Step 4: Create `docker/functional/asterisk-config/ari.conf`**

```ini
[general]
enabled = yes
pretty = yes
allowed_origins = *

[testari]
type = user
password = testari
read_only = no
```

- [ ] **Step 5: Replace `docker/functional/asterisk-config/extensions.conf` with unified dialplan**

```ini
[general]
static = yes
writeprotect = no

[globals]

; Integration + functional test context
[default]
exten => _X.,1,NoOp(Default: ${EXTEN})
 same => n,Answer()
 same => n,Wait(5)
 same => n,Hangup()

; Stasis app for ARI tests
[stasis-test]
exten => _X.,1,NoOp(Stasis: ${EXTEN})
 same => n,Answer()
 same => n,Stasis(test-app)
 same => n,Hangup()

; Queue dialplan for queue functional tests
[queue-test]
exten => _X.,1,NoOp(Queue: ${EXTEN})
 same => n,Answer()
 same => n,Queue(${EXTEN},,,,30)
 same => n,Hangup()

; Echo test
exten => echo,1,Answer()
 same => n,Echo()
 same => n,Hangup()
```

- [ ] **Step 6: Replace `docker/functional/asterisk-config/modules.conf` to load ARI modules**

```ini
[modules]
autoload = yes

; PostgreSQL must load before realtime-dependent modules
preload = res_config_pgsql.so
preload = res_sorcery_config.so
preload = res_sorcery_memory.so
preload = res_sorcery_memory_cache.so
preload = res_sorcery_realtime.so

; Drivers not needed
noload = res_config_odbc.so
noload = res_odbc.so
noload = res_config_ldap.so
noload = res_config_sqlite3.so

; Legacy channel drivers disabled
noload = chan_sip.so
noload = chan_skinny.so
noload = chan_mgcp.so
```

- [ ] **Step 7: Keep these realtime-config files as-is (already correct):**
`asterisk.conf`, `cdr.conf`, `extconfig.conf`, `logger.conf`, `pjsip.conf`, `queues.conf`, `res_pgsql.conf`, `rtp.conf`, `sorcery.conf`, `stasis.conf`.

- [ ] **Step 8: Add `docker/functional/asterisk-config/confbridge.conf`** (was in old test-config, needed for ConfBridge tests)

```ini
[general]

[default_user]
type = user
announce_user_count = yes

[default_bridge]
type = bridge
max_members = 50
```

- [ ] **Step 9: Add `docker/functional/asterisk-config/res_parking.conf`** (was in old test-config, needed for parking tests)

```ini
[general]

[default]
context = default
parkedcallreparking = caller
parkedcallhangup = caller
```

- [ ] **Step 10: Add `docker/functional/asterisk-config/voicemail.conf`**

```ini
[general]
format = wav49|gsm|wav

[default]
```

- [ ] **Step 11: Verify all files listed and delete the old directory**

Run: `ls docker/functional/asterisk-config/ | sort`
Expected lines (16 files): `ari.conf asterisk.conf cdr.conf confbridge.conf extconfig.conf extensions.conf http.conf logger.conf manager.conf modules.conf pjsip.conf queues.conf res_parking.conf res_pgsql.conf rtp.conf sorcery.conf stasis.conf voicemail.conf`

Then: `rm -rf docker/functional/asterisk-realtime-config`

- [ ] **Step 12: Commit**

```bash
git add docker/functional/asterisk-config/ docker/functional/asterisk-realtime-config
git commit -m "feat(docker): unify realtime + ARI config into asterisk-config/"
```

---

## Task 5: Delete old Dockerfile.asterisk-realtime and test-config

**Files:**
- Delete: `docker/Dockerfile.asterisk-realtime`
- Delete: `docker/test-config/` (14 files)

- [ ] **Step 1: Remove**

```bash
git rm docker/Dockerfile.asterisk-realtime
git rm -r docker/test-config
```

- [ ] **Step 2: Verify no remaining references in code**

Run: `grep -rE "test-config|Dockerfile.asterisk-realtime" docker/ Tests/ --include="*.cs" --include="*.yml" --include="*.sh"`
Expected: no matches. If any found, they belong to upcoming tasks that will remove them — record the file names.

- [ ] **Step 3: Commit**

```bash
git commit -m "chore(docker): remove obsolete test-config and realtime Dockerfile"
```

---

## Task 6: Update `DockerPaths.cs`

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/DockerPaths.cs`

- [ ] **Step 1: Replace file contents**

```csharp
namespace Asterisk.Sdk.TestInfrastructure;

/// <summary>Resolves Docker-related paths relative to the solution root.</summary>
public static class DockerPaths
{
    private static readonly Lazy<string> _solutionRoot = new(FindSolutionRoot);

    public static string SolutionRoot => _solutionRoot.Value;
    public static string DockerDir => Path.Combine(SolutionRoot, "docker");
    public static string FunctionalDir => Path.Combine(DockerDir, "functional");
    public static string AsteriskConfig => Path.Combine(FunctionalDir, "asterisk-config");
    public static string PstnEmulatorConfig => Path.Combine(FunctionalDir, "pstn-emulator-config");
    public static string AsteriskDockerfile => Path.Combine(DockerDir, "Dockerfile.asterisk");
    public static string FunctionalSqlDir => Path.Combine(FunctionalDir, "sql");
    public static string SippScenariosDir => Path.Combine(FunctionalDir, "sipp-scenarios");

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Asterisk.Sdk.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find solution root (Asterisk.Sdk.slnx)");
    }
}
```

- [ ] **Step 2: Build test infra to catch compile errors**

Run: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/ -c Release`
Expected: compile errors referencing `FunctionalAsteriskConfig`, `AsteriskFileDockerfile`, `AsteriskRealtimeDockerfile` in the 3 container files. These are fixed in Task 7.

---

## Task 7: Refactor `AsteriskContainer` to realtime mode

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs`
- Delete: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskRealtimeContainer.cs`

- [ ] **Step 1: Replace `AsteriskContainer.cs` content**

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>
/// Wraps the unified Asterisk 22 container running in Realtime mode
/// (PostgreSQL-backed PJSIP). Requires a shared network with PostgresContainer.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public int AriPort => _container.GetMappedPublicPort(8088);
    public int AgiPort => _container.GetMappedPublicPort(4573);
    public string ContainerName => _container.Name;

    public AsteriskContainer(INetwork network)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            .WithPortBinding(4573, true)
            .WithBindMount(DockerPaths.AsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("asterisk")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(5038)
                    .UntilPortIsAvailable(8088))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 2: Delete `AsteriskRealtimeContainer.cs`**

```bash
git rm Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskRealtimeContainer.cs
```

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs \
        Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskRealtimeContainer.cs \
        Tests/Asterisk.Sdk.TestInfrastructure/DockerPaths.cs
git commit -m "refactor(test-infra): unify AsteriskContainer on realtime mode"
```

---

## Task 8: Update `PstnEmulatorContainer` to use unified image

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PstnEmulatorContainer.cs`

- [ ] **Step 1: Replace file content**

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>
/// Wraps a PSTN emulator container using the unified Asterisk image with file-based
/// pstn-emulator-config. Must share a network with the main Asterisk container.
/// Does NOT require Postgres — runs in file mode.
/// </summary>
public sealed class PstnEmulatorContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public string ContainerName => _container.Name;

    public PstnEmulatorContainer(INetwork network)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithBindMount(DockerPaths.PstnEmulatorConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("pstn-emulator")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(5038))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 2: Commit**

```bash
git add Tests/Asterisk.Sdk.TestInfrastructure/Containers/PstnEmulatorContainer.cs
git commit -m "refactor(test-infra): use unified image for PSTN emulator"
```

---

## Task 9: Update `PostgresContainer` alias and `IntegrationFixture`

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PostgresContainer.cs`
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/IntegrationFixture.cs`

- [ ] **Step 0: Add network alias to `PostgresContainer.cs`**

Asterisk's `res_pgsql.conf` hardcodes `dbhost=postgres`, so the Postgres container must expose that network alias. Modify `PostgresContainer.cs` line 37-38 block:

```csharp
        if (network is not null)
            builder = builder.WithNetwork(network).WithNetworkAliases("postgres");

        _container = builder.Build();
```

Verify build: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/ -c Release`
Expected: compiles clean.

- [ ] **Step 1: Replace file content**

```csharp
using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Integration fixture: PostgreSQL (for Realtime) + Asterisk (unified, realtime mode).
/// PostgreSQL starts first so Asterisk can connect on boot.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; }

    public IntegrationFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);
        await Postgres.StartAsync().ConfigureAwait(false);
        await Asterisk.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync().ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Tests/Asterisk.Sdk.TestInfrastructure/Stacks/IntegrationFixture.cs
git commit -m "refactor(test-infra): add Postgres to IntegrationFixture"
```

---

## Task 10: Update `FunctionalFixture` and `RealtimeFixture`

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/FunctionalFixture.cs`
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/RealtimeFixture.cs`

- [ ] **Step 1: Replace `FunctionalFixture.cs`**

```csharp
using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Full functional fixture: Postgres (realtime DB) + Asterisk (realtime) + PSTN emulator (file) + Toxiproxy + SIPp.
/// Postgres starts first, then Asterisk + PstnEmulator + Toxiproxy in parallel, then SIPp.
/// </summary>
public sealed class FunctionalFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; }
    public PstnEmulatorContainer PstnEmulator { get; }
    public ToxiproxyContainer Toxiproxy { get; }
    public SippContainer Sipp { get; }

    public FunctionalFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskContainer(_network);
        PstnEmulator = new PstnEmulatorContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
        Sipp = new SippContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        // Postgres must be ready before Asterisk realtime can connect
        await Postgres.StartAsync().ConfigureAwait(false);

        // Asterisk, PstnEmulator, and Toxiproxy start in parallel
        await Task.WhenAll(
            Asterisk.StartAsync(),
            PstnEmulator.StartAsync(),
            Toxiproxy.StartAsync()).ConfigureAwait(false);

        // SIPp needs Asterisk ready before it can dial
        await Sipp.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Sipp.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(
            Toxiproxy.DisposeAsync().AsTask(),
            PstnEmulator.DisposeAsync().AsTask(),
            Asterisk.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: Replace `RealtimeFixture.cs`**

```csharp
using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Realtime fixture: shared network, PostgreSQL + Asterisk (realtime mode).
/// Equivalent to the Asterisk half of FunctionalFixture, minus PSTN/SIPp/Toxiproxy.
/// </summary>
public sealed class RealtimeFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; }

    public RealtimeFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);
        await Postgres.StartAsync().ConfigureAwait(false);
        await Asterisk.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync().ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: Update `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/RealtimeFixture.cs`**

Check that the wrapper still compiles against the refactored `RealtimeFixture`. The wrapper in this file reads `_stack.Asterisk.Host` and `_stack.Asterisk.AmiPort` — these properties still exist on the new `AsteriskContainer`, so no code changes expected. Just build and verify:

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/ -c Release`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.TestInfrastructure/Stacks/
git commit -m "refactor(test-infra): FunctionalFixture + RealtimeFixture use unified AsteriskContainer"
```

---

## Task 11: Rewrite `docker-compose.test.yml` (dev convenience)

**Files:**
- Modify: `docker/docker-compose.test.yml`

This file is for devs who want to spin up the stack manually (not used by Testcontainers). Update to use the new Dockerfile.

- [ ] **Step 1: Replace file content**

```yaml
networks:
  sdk-test:
    name: sdk-test-net

services:
  postgres:
    image: postgres:17-alpine
    container_name: asterisk-sdk-test-db
    networks:
      - sdk-test
    environment:
      POSTGRES_USER: asterisk
      POSTGRES_PASSWORD: asterisk
      POSTGRES_DB: asterisk
    ports:
      - "15432:5432"
    volumes:
      - ./functional/sql:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U asterisk"]
      interval: 3s
      timeout: 2s
      retries: 10
      start_period: 5s

  asterisk:
    build:
      context: .
      dockerfile: Dockerfile.asterisk
    container_name: asterisk-sdk-test
    networks:
      - sdk-test
    depends_on:
      postgres:
        condition: service_healthy
    ports:
      - "15038:5038"
      - "15088:8088"
      - "14573:4573"
    volumes:
      - ./functional/asterisk-config:/etc/asterisk:ro
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show uptime"]
      interval: 5s
      timeout: 3s
      retries: 10
      start_period: 15s

  pstn-emulator:
    build:
      context: .
      dockerfile: Dockerfile.asterisk
    container_name: asterisk-sdk-test-pstn
    networks:
      - sdk-test
    ports:
      - "15039:5038"
    volumes:
      - ./functional/pstn-emulator-config:/etc/asterisk:ro
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show uptime"]
      interval: 5s
      timeout: 3s
      retries: 10
      start_period: 10s

  toxiproxy:
    image: ghcr.io/shopify/toxiproxy:2.9.0
    container_name: asterisk-sdk-test-toxiproxy
    networks:
      - sdk-test
    ports:
      - "8474:8474"
      - "15138:15038"
    depends_on:
      asterisk:
        condition: service_healthy
```

- [ ] **Step 2: Validate compose file syntax**

Run: `docker compose -f docker/docker-compose.test.yml config >/dev/null`
Expected: no output (success). Any stderr means YAML error.

- [ ] **Step 3: Commit**

```bash
git add docker/docker-compose.test.yml
git commit -m "chore(docker): update compose for unified Dockerfile build"
```

---

## Task 12: Verify full build still passes

**Files:** none (verification)

- [ ] **Step 1: Clean build**

Run: `dotnet build Asterisk.Sdk.slnx -c Release --no-incremental 2>&1 | tail -5`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s)`.

- [ ] **Step 2: Unit tests (no Docker)**

Run: `dotnet test Asterisk.Sdk.slnx -c Release --no-build --filter "Category!=Functional&Category!=Integration&Category!=Realtime" 2>&1 | tail -5`
Expected: all projects `Passed!`, total ~2591, `Failed: 0`.

- [ ] **Step 3: If anything fails, fix before continuing.** Do not commit workarounds.

---

## Task 13: Run full functional + integration suite

**Files:** none (verification)

- [ ] **Step 1: Ensure Docker is running and no conflicting containers**

Run: `docker ps --format '{{.Names}}' | grep -E "^(asterisk-sdk-test|demo-asterisk|pro-asterisk)"`
If any: stop them. Testcontainers maps random host ports, but a zombie container with `container_name` could clash.

- [ ] **Step 2: Run IntegrationTests**

Run: `dotnet test Tests/Asterisk.Sdk.IntegrationTests/ -c Release --no-build --logger "console;verbosity=minimal" 2>&1 | tail -20`
Expected: all tests pass. Record pass count.

- [ ] **Step 3: Run FunctionalTests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ -c Release --no-build --logger "console;verbosity=minimal" 2>&1 | tail -30`
Expected: all tests pass (both `Functional` and `Realtime` collections). First run downloads sounds (~10 min for image build); subsequent runs are fast (image cached).

- [ ] **Step 4: Run Sessions.FunctionalTests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.FunctionalTests/ -c Release --no-build --logger "console;verbosity=minimal" 2>&1 | tail -10`
Expected: 37 tests pass (matches memory baseline).

- [ ] **Step 5: If failures, diagnose**

For each failed test:
1. Read the test and check which fixture it uses.
2. Check the container logs: `docker logs <container-name>`.
3. Most likely causes: missing config in `asterisk-config/` (add to Task 4 step), credentials mismatch (fix manager.conf), missing dialplan extension (extend extensions.conf).
4. Fix root cause, re-run. Do not use `[Skip]` on failing tests.

- [ ] **Step 6: Commit any fixes**

If Task 4/5 config needed extension to make tests pass, commit under:
```bash
git commit -m "fix(docker): <specific fix applied>"
```

---

## Task 14: Remove CI `continue-on-error` workaround

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Edit the workflow**

Remove BOTH occurrences of `continue-on-error: true` in the `functional-tests` job. Final job should look like:

```yaml
  functional-tests:
    name: Functional Tests (Testcontainers)
    runs-on: ubuntu-latest
    needs: unit-tests
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Run functional + integration tests
        run: dotnet test Asterisk.Sdk.slnx --filter "Category=Functional|Category=Integration|Category=Realtime" --logger "console;verbosity=detailed"
        timeout-minutes: 30
```

Note: bumped `timeout-minutes` from 15 → 30 because first CI run builds the heavy image with sounds.

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: enforce functional tests (remove continue-on-error)"
```

---

## Task 15: Push branch and open PR

**Files:** none (git + GitHub)

- [ ] **Step 1: Push branch**

Ask user for confirmation before pushing (per global memory rule: "user confirms explicitly before each push").

Then:
```bash
git push -u origin chore/docker-unify-realtime
```

- [ ] **Step 2: Open PR**

```bash
gh pr create --base main --title "chore(docker): unify test infrastructure on realtime Asterisk 22" --body "$(cat <<'EOF'
## Summary

- Replace two Dockerfiles (`Dockerfile.asterisk-file` missing since 2026-03-24, `Dockerfile.asterisk-realtime`) with a single `docker/Dockerfile.asterisk` based on `andrius/asterisk:22` bundling `codec_opus` and EN+ES sounds.
- Main Asterisk test container now runs in **Realtime mode** (PostgreSQL-backed PJSIP). PSTN emulator remains **file-based** (same image, different config mount).
- Unify the old `test-config/` and `asterisk-realtime-config/` into a single `docker/functional/asterisk-config/` with ARI enabled, shared credentials (`testadmin`/`testpass`, `dashboard`/`dashboard`, `testari`/`testari`).
- Restore `docker/functional/pstn-emulator-config/` from git (deleted in `33a7dae`).
- Refactor `AsteriskContainer` to require a shared network with `PostgresContainer`. Delete `AsteriskRealtimeContainer` (merged). Update `IntegrationFixture` and `FunctionalFixture` to include Postgres.
- Remove `continue-on-error: true` from the functional-tests job in CI.

## Motivation

During the v1.8.0 audit we found that the file-based Dockerfile was silently deleted in the PbxAdmin-extraction commit, leaving Testcontainers unable to build the image. Functional tests were secretly failing for 20+ days, masked by the `continue-on-error` flag in CI. This PR restores the functional suite and aligns test infrastructure with the realtime deployment pattern used in production (Platform).

## Test plan

- [ ] `dotnet build Asterisk.Sdk.slnx -c Release` — 0 warnings, 0 errors
- [ ] Unit tests pass: `dotnet test --filter "Category!=Functional&Category!=Integration&Category!=Realtime"`
- [ ] Integration tests pass: `dotnet test Tests/Asterisk.Sdk.IntegrationTests/`
- [ ] Functional tests pass: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/`
- [ ] Sessions functional tests pass: `dotnet test Tests/Asterisk.Sdk.Sessions.FunctionalTests/`
- [ ] CI `functional-tests` job now enforced (no `continue-on-error`)
- [ ] `docker compose -f docker/docker-compose.test.yml config` validates
EOF
)"
```

- [ ] **Step 3: Record PR URL**

Output the PR URL for user.

---

## Verification summary

After all tasks complete, the user should see:
- `docker/Dockerfile.asterisk` (single image)
- `docker/functional/asterisk-config/` (16 files, realtime + ARI)
- `docker/functional/pstn-emulator-config/` (5 files, file-based)
- `docker/functional/sql/001_schema.sql` (pre-existing, unchanged)
- NO `docker/Dockerfile.asterisk-realtime`, NO `docker/test-config/`
- Green CI pipeline on the PR, no `continue-on-error` on functional-tests

## Rollback

If this PR causes regressions post-merge, the previous state is tag `v1.8.0` (commit `64289d6`). All changes here are contained to `docker/`, `Tests/Asterisk.Sdk.TestInfrastructure/`, and `.github/workflows/ci.yml` — no src/ changes.

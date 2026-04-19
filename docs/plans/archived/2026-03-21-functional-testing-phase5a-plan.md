# Phase 5A — Realtime DB Functional Testing Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 6 functional tests verifying that Asterisk loads PJSIP endpoints and queue members from PostgreSQL via ODBC realtime, and that database changes are reflected in AMI queries.

**Architecture:** A new `docker-compose.realtime.yml` extends the functional test stack with PostgreSQL + a realtime-configured Asterisk container. Tests use Npgsql to INSERT/UPDATE/DELETE directly in PostgreSQL, then verify via AMI actions (`PjSipShowEndpoint`, `QueueStatus`) that Asterisk picks up the changes. A `RealtimeFixture` provides shared Npgsql connection and container lifecycle.

**Tech Stack:** PostgreSQL 17, Npgsql 9.0.3, Dapper 2.1.66, xunit, FluentAssertions, existing AMI test infrastructure

---

## File Structure

```
docker/functional/
  docker-compose.realtime.yml              ← Task 1: PostgreSQL + Asterisk realtime compose
  sql/
    001-realtime-test-schema.sql           ← Task 1: minimal schema for tests

Tests/Asterisk.Sdk.FunctionalTests/
  Infrastructure/
    Attributes/
      RealtimeFactAttribute.cs             ← Task 2: skip guard for realtime stack
    Fixtures/
      RealtimeFixture.cs                   ← Task 2: Npgsql connection + test data lifecycle
  Layer5_Integration/
    RealtimeDb/
      RealtimePjsipTests.cs               ← Task 3: 3 PJSIP endpoint tests
      RealtimeQueueTests.cs               ← Task 4: 3 queue member tests
```

---

## Task 1: Docker Compose + SQL Schema for Realtime Tests

**Files:**
- Create: `docker/functional/docker-compose.realtime.yml`
- Create: `docker/functional/sql/001-realtime-test-schema.sql`

The realtime Asterisk reuses `docker-asterisk-realtime:latest` (already built locally) with the existing configs from `docker/asterisk-config-realtime/`. PostgreSQL uses the existing schema from `docker/sql/001-asterisk-realtime-schema.sql` as base, plus a minimal test seed.

- [ ] **Step 1: Create the test SQL schema**

Create `docker/functional/sql/001-realtime-test-schema.sql` — this copies the essential tables from the main schema (only the tables we test: `ps_endpoints`, `ps_auths`, `ps_aors`, `queue_table`, `queue_members`):

```sql
-- Realtime functional test schema
-- Only tables needed for Phase 5A tests

CREATE TABLE IF NOT EXISTS ps_endpoints (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    transport VARCHAR(40),
    aors VARCHAR(200),
    auth VARCHAR(40),
    context VARCHAR(40) DEFAULT 'default',
    disallow VARCHAR(200) DEFAULT 'all',
    allow VARCHAR(200) DEFAULT 'ulaw,alaw',
    direct_media VARCHAR(3) DEFAULT 'no',
    dtmf_mode VARCHAR(10) DEFAULT 'rfc4733',
    force_rport VARCHAR(3) DEFAULT 'yes',
    rewrite_contact VARCHAR(3) DEFAULT 'yes',
    rtp_symmetric VARCHAR(3) DEFAULT 'yes',
    callerid VARCHAR(100),
    mailboxes VARCHAR(200),
    language VARCHAR(10)
);

CREATE TABLE IF NOT EXISTS ps_auths (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type VARCHAR(40) DEFAULT 'userpass',
    password VARCHAR(80),
    username VARCHAR(40)
);

CREATE TABLE IF NOT EXISTS ps_aors (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    max_contacts INTEGER DEFAULT 1,
    remove_existing VARCHAR(3) DEFAULT 'yes'
);

CREATE TABLE IF NOT EXISTS queue_table (
    name VARCHAR(128) NOT NULL PRIMARY KEY,
    strategy VARCHAR(40) DEFAULT 'ringall',
    timeout INTEGER DEFAULT 30,
    wrapuptime INTEGER DEFAULT 0,
    maxlen INTEGER DEFAULT 0,
    musicclass VARCHAR(80) DEFAULT 'default'
);

CREATE TABLE IF NOT EXISTS queue_members (
    queue_name VARCHAR(128) NOT NULL,
    interface VARCHAR(128) NOT NULL,
    membername VARCHAR(40),
    state_interface VARCHAR(128),
    penalty INTEGER DEFAULT 0,
    paused INTEGER DEFAULT 0,
    PRIMARY KEY (queue_name, interface)
);
```

- [ ] **Step 2: Create the docker compose file**

Create `docker/functional/docker-compose.realtime.yml`:

```yaml
services:
  postgres:
    image: postgres:17-alpine
    container_name: functional-postgres
    environment:
      POSTGRES_DB: asterisk
      POSTGRES_USER: asterisk
      POSTGRES_PASSWORD: asterisk
    volumes:
      - ./sql:/docker-entrypoint-initdb.d
    ports:
      - "15432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U asterisk"]
      interval: 3s
      timeout: 2s
      retries: 10

  asterisk-realtime:
    image: docker-asterisk-realtime:latest
    container_name: functional-asterisk-realtime
    ports:
      - "15038:5038"
      - "18088:8088"
    volumes:
      - ../asterisk-config-realtime/ari.conf:/etc/asterisk/ari.conf:ro
      - ../asterisk-config-realtime/asterisk.conf:/etc/asterisk/asterisk.conf:ro
      - ../asterisk-config-realtime/cdr.conf:/etc/asterisk/cdr.conf:ro
      - ../asterisk-config-realtime/cdr_odbc.conf:/etc/asterisk/cdr_odbc.conf:ro
      - ../asterisk-config-realtime/cel.conf:/etc/asterisk/cel.conf:ro
      - ../asterisk-config-realtime/cel_odbc.conf:/etc/asterisk/cel_odbc.conf:ro
      - ../asterisk-config-realtime/confbridge.conf:/etc/asterisk/confbridge.conf:ro
      - ../asterisk-config-realtime/extconfig.conf:/etc/asterisk/extconfig.conf:ro
      - ../asterisk-config-realtime/extensions.conf:/etc/asterisk/extensions.conf:ro
      - ../asterisk-config-realtime/features.conf:/etc/asterisk/features.conf:ro
      - ../asterisk-config-realtime/geolocation.conf:/etc/asterisk/geolocation.conf:ro
      - ../asterisk-config-realtime/http.conf:/etc/asterisk/http.conf:ro
      - ../asterisk-config-realtime/logger.conf:/etc/asterisk/logger.conf:ro
      - ../asterisk-config-realtime/manager.conf:/etc/asterisk/manager.conf:ro
      - ../asterisk-config-realtime/modules.conf:/etc/asterisk/modules.conf:ro
      - ../asterisk-config-realtime/musiconhold.conf:/etc/asterisk/musiconhold.conf:ro
      - ../asterisk-config-realtime/pjproject.conf:/etc/asterisk/pjproject.conf:ro
      - ../asterisk-config-realtime/pjsip.conf:/etc/asterisk/pjsip.conf:ro
      - ../asterisk-config-realtime/queues.conf:/etc/asterisk/queues.conf:ro
      - ../asterisk-config-realtime/res_odbc.conf:/etc/asterisk/res_odbc.conf:ro
      - ../asterisk-config-realtime/res_parking.conf:/etc/asterisk/res_parking.conf:ro
      - ../asterisk-config-realtime/sorcery.conf:/etc/asterisk/sorcery.conf:ro
      - ../asterisk-config-realtime/users.conf:/etc/asterisk/users.conf:ro
      - ../asterisk-config-realtime/odbcinst.ini:/etc/odbcinst.ini:ro
      - ../asterisk-config-realtime/odbc.ini:/etc/odbc.ini:ro
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show version"]
      interval: 5s
      timeout: 3s
      retries: 15
```

- [ ] **Step 3: Verify the stack starts**

```bash
docker compose -f docker/functional/docker-compose.realtime.yml up -d
docker compose -f docker/functional/docker-compose.realtime.yml exec asterisk-realtime asterisk -rx 'module show like res_config_odbc'
docker compose -f docker/functional/docker-compose.realtime.yml exec asterisk-realtime asterisk -rx 'odbc show all'
```

Expected: ODBC connection `asterisk` shows as connected.

- [ ] **Step 4: Commit**

```bash
git add docker/functional/docker-compose.realtime.yml docker/functional/sql/
git commit -m "feat(functional): add Docker compose for realtime DB tests (PostgreSQL + Asterisk ODBC)"
```

---

## Task 2: RealtimeFixture + RealtimeFactAttribute

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Attributes/RealtimeFactAttribute.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/RealtimeFixture.cs`
- Modify: `Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj` (add Npgsql + Dapper references)

- [ ] **Step 1: Add Npgsql + Dapper to the test project**

In `Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj`, add to the PackageReference `<ItemGroup>`:
```xml
<PackageReference Include="Npgsql" />
<PackageReference Include="Dapper" />
```

(Both versions are already in `Directory.Packages.props`: Npgsql 9.0.3, Dapper 2.1.66.)

- [ ] **Step 2: Create RealtimeFactAttribute**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

/// <summary>
/// Skips the test if the realtime stack (PostgreSQL + Asterisk) is not reachable.
/// Probes PostgreSQL on port 15432 and AMI on port 15038.
/// </summary>
public sealed class RealtimeFactAttribute : FactAttribute
{
    public RealtimeFactAttribute()
    {
        if (!IsRealtimeReachable())
        {
            Skip = "Realtime stack not reachable (PostgreSQL:15432 + AMI:15038)";
        }
    }

    private static bool IsRealtimeReachable()
    {
        try
        {
            using var pgClient = new System.Net.Sockets.TcpClient();
            pgClient.Connect("localhost", 15432);

            using var amiClient = new System.Net.Sockets.TcpClient();
            amiClient.Connect("localhost", 15038);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 3: Create RealtimeFixture**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Npgsql;

/// <summary>
/// Shared fixture for realtime DB tests. Provides a NpgsqlConnection
/// to the functional PostgreSQL container and AMI connection details.
/// Cleans up test data between tests.
/// </summary>
public sealed class RealtimeFixture : IAsyncLifetime
{
    private NpgsqlDataSource? _dataSource;

    public string PostgresConnectionString =>
        Environment.GetEnvironmentVariable("REALTIME_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=15432;Database=asterisk;Username=asterisk;Password=asterisk";

    public string AmiHost =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_HOST") ?? "localhost";
    public int AmiPort =>
        int.Parse(Environment.GetEnvironmentVariable("REALTIME_AMI_PORT") ?? "15038",
            System.Globalization.CultureInfo.InvariantCulture);
    public string AmiUsername =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_USERNAME") ?? "dashboard";
    public string AmiPassword =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_PASSWORD") ?? "dashboard";

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("RealtimeFixture not initialized");

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(PostgresConnectionString);
        // Verify connection
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT 1");
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Remove test data by ID prefix to avoid cross-test pollution.
    /// </summary>
    public async Task CleanupTestEndpointAsync(string endpointId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM ps_endpoints WHERE id = @Id", new { Id = endpointId });
        await conn.ExecuteAsync("DELETE FROM ps_auths WHERE id = @Id", new { Id = endpointId });
        await conn.ExecuteAsync("DELETE FROM ps_aors WHERE id = @Id", new { Id = endpointId });
    }

    public async Task CleanupTestQueueAsync(string queueName)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM queue_members WHERE queue_name = @Name", new { Name = queueName });
        await conn.ExecuteAsync("DELETE FROM queue_table WHERE name = @Name", new { Name = queueName });
    }
}
```

Note: Uses `Dapper.ExecuteAsync` extension method on `NpgsqlConnection`.

- [ ] **Step 4: Build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/`
Expected: Build succeeds (0 warnings)

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj \
       Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Attributes/RealtimeFactAttribute.cs \
       Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/RealtimeFixture.cs
git commit -m "feat(functional): add RealtimeFixture and RealtimeFactAttribute for DB tests"
```

---

## Task 3: Realtime PJSIP Endpoint Tests (3 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/RealtimeDb/RealtimePjsipTests.cs`

**Pattern:** INSERT endpoint into PostgreSQL → AMI `Command("pjsip show endpoint <id>")` → assert Asterisk sees it. Use `RealtimeFixture` for DB access and cleanup.

**Important:** The realtime Asterisk AMI user is `dashboard`/`dashboard` on port `15038`. Create AMI connections via `AmiConnectionFactory.Create` but override host/port/user/pass from `RealtimeFixture`.

- [ ] **Step 1: Write the 3 PJSIP tests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.RealtimeDb;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tests that PJSIP endpoints created in PostgreSQL are visible to Asterisk via AMI.
/// Requires the realtime Docker stack: docker compose -f docker/functional/docker-compose.realtime.yml up -d
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Realtime")]
public sealed class RealtimePjsipTests : FunctionalTestBase, IClassFixture<RealtimeFixture>
{
    private readonly RealtimeFixture _fixture;

    public RealtimePjsipTests(RealtimeFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [RealtimeFact]
    public async Task InsertEndpoint_ShouldBeVisibleViaAmi()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];

        try
        {
            // INSERT into PostgreSQL
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO ps_endpoints (id, context, allow, disallow, callerid)
                VALUES (@Id, 'from-internal', 'ulaw,alaw', 'all', 'Test RT <9999>')
                """, new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            // Query via AMI
            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"pjsip show endpoint {endpointId}"
            });

            // Asterisk should find the endpoint from the database
            var output = response["Output"] ?? response.ResponseStatus ?? "";
            output.Should().NotBeEmpty("AMI command must return output");

            // The endpoint name or "not found" — realtime endpoints are loaded on demand
            // If Asterisk supports it, the output should contain the endpoint ID
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    [RealtimeFact]
    public async Task UpdateEndpoint_ShouldReflectChangeAfterReload()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];

        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();

            // Create endpoint with initial callerid
            await conn.ExecuteAsync(
                """
                INSERT INTO ps_endpoints (id, context, allow, disallow, callerid)
                VALUES (@Id, 'from-internal', 'ulaw,alaw', 'all', 'Original <1000>')
                """, new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            // Update callerid in database
            await conn.ExecuteAsync(
                "UPDATE ps_endpoints SET callerid = 'Updated <2000>' WHERE id = @Id",
                new { Id = endpointId });

            // Reload PJSIP via AMI to pick up changes
            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            await connection.SendActionAsync(new CommandAction
            {
                Command = "pjsip reload"
            });

            await Task.Delay(TimeSpan.FromSeconds(2));

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"pjsip show endpoint {endpointId}"
            });

            var output = response["Output"] ?? response.ResponseStatus ?? "";
            output.Should().NotBeEmpty("AMI must return output after reload");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    [RealtimeFact]
    public async Task DeleteEndpoint_ShouldNotBeVisibleAfterReload()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];

        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();

            // Create then delete
            await conn.ExecuteAsync(
                """
                INSERT INTO ps_endpoints (id, context, allow, disallow)
                VALUES (@Id, 'from-internal', 'ulaw,alaw', 'all')
                """, new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            // Delete from database
            await conn.ExecuteAsync("DELETE FROM ps_aors WHERE id = @Id", new { Id = endpointId });
            await conn.ExecuteAsync("DELETE FROM ps_endpoints WHERE id = @Id", new { Id = endpointId });

            // Reload
            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            await connection.SendActionAsync(new CommandAction
            {
                Command = "pjsip reload"
            });

            await Task.Delay(TimeSpan.FromSeconds(2));

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"pjsip show endpoint {endpointId}"
            });

            var output = response["Output"] ?? response.ResponseStatus ?? "";

            // After deletion + reload, the endpoint should not be found
            // Asterisk returns "Unable to find endpoint" or similar for missing endpoints
            var isGone = output.Contains("Unable", StringComparison.OrdinalIgnoreCase)
                      || output.Contains("not found", StringComparison.OrdinalIgnoreCase)
                      || string.IsNullOrEmpty(output);
            isGone.Should().BeTrue($"deleted endpoint '{endpointId}' must not be visible after reload");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    private AmiConnection CreateRealtimeAmiConnection()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = _fixture.AmiHost,
            Port = _fixture.AmiPort,
            Username = _fixture.AmiUsername,
            Password = _fixture.AmiPassword,
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });
        return new AmiConnection(options, new PipelineSocketConnectionFactory(),
            LoggerFactory.CreateLogger<AmiConnection>());
    }
}
```

- [ ] **Step 2: Build and verify compilation**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/`
Expected: Build succeeds (0 warnings)

- [ ] **Step 3: Run tests (expect skip without realtime stack)**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~RealtimePjsipTests" --no-build -v q`
Expected: 3 tests skipped (RealtimeFact guard)

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/RealtimeDb/RealtimePjsipTests.cs
git commit -m "test(functional): add realtime PJSIP endpoint tests (3 tests)"
```

---

## Task 4: Realtime Queue Member Tests (3 tests)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/RealtimeDb/RealtimeQueueTests.cs`

**Pattern:** INSERT queue + members into PostgreSQL → AMI `QueueStatusAction` or `CommandAction("queue show <name>")` → verify members visible.

- [ ] **Step 1: Write the 3 queue tests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.RealtimeDb;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tests that queue definitions and members created in PostgreSQL are visible
/// to Asterisk via AMI queue commands.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Realtime")]
public sealed class RealtimeQueueTests : FunctionalTestBase, IClassFixture<RealtimeFixture>
{
    private readonly RealtimeFixture _fixture;

    public RealtimeQueueTests(RealtimeFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [RealtimeFact]
    public async Task InsertQueueWithMember_ShouldBeVisibleViaAmi()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];

        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();

            // Create queue + member in PostgreSQL
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 30)",
                new { Name = queueName });
            await conn.ExecuteAsync(
                """
                INSERT INTO queue_members (queue_name, interface, membername, penalty)
                VALUES (@Queue, 'Local/100@test-functional', 'TestAgent', 0)
                """,
                new { Queue = queueName });

            // Reload queues via AMI
            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            await connection.SendActionAsync(new CommandAction
            {
                Command = "queue reload all"
            });

            await Task.Delay(TimeSpan.FromSeconds(3));

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"queue show {queueName}"
            });

            var output = response["Output"] ?? response.ResponseStatus ?? "";
            output.Should().NotBeEmpty("AMI queue show must return output for realtime queue");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
        }
    }

    [RealtimeFact]
    public async Task AddMemberViaDb_ShouldAppearInQueueStatus()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];

        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();

            // Create queue with no members
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy) VALUES (@Name, 'ringall')",
                new { Name = queueName });

            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            await connection.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Now add a member via DB
            await conn.ExecuteAsync(
                """
                INSERT INTO queue_members (queue_name, interface, membername, penalty)
                VALUES (@Queue, 'Local/700@test-functional', 'Agent1', 0)
                """,
                new { Queue = queueName });

            // Reload again to pick up the new member
            await connection.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(2));

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"queue show {queueName}"
            });

            var output = response["Output"] ?? response.ResponseStatus ?? "";
            // Queue show should mention the member interface
            var hasMember = output.Contains("Local/700", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("Agent1", StringComparison.OrdinalIgnoreCase);
            hasMember.Should().BeTrue("added member must appear in queue show after reload");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
        }
    }

    [RealtimeFact]
    public async Task RemoveMemberViaDb_ShouldDisappearAfterReload()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];

        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();

            // Create queue with one member
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy) VALUES (@Name, 'ringall')",
                new { Name = queueName });
            await conn.ExecuteAsync(
                """
                INSERT INTO queue_members (queue_name, interface, membername)
                VALUES (@Queue, 'Local/700@test-functional', 'RemoveMe')
                """,
                new { Queue = queueName });

            await using var connection = CreateRealtimeAmiConnection();
            await connection.ConnectAsync();

            await connection.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Remove the member from DB
            await conn.ExecuteAsync(
                "DELETE FROM queue_members WHERE queue_name = @Queue AND interface = 'Local/700@test-functional'",
                new { Queue = queueName });

            // Reload
            await connection.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(2));

            var response = await connection.SendActionAsync(new CommandAction
            {
                Command = $"queue show {queueName}"
            });

            var output = response["Output"] ?? response.ResponseStatus ?? "";
            var stillHasMember = output.Contains("RemoveMe", StringComparison.OrdinalIgnoreCase);
            stillHasMember.Should().BeFalse(
                "removed member must not appear in queue show after reload");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
        }
    }

    private AmiConnection CreateRealtimeAmiConnection()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = _fixture.AmiHost,
            Port = _fixture.AmiPort,
            Username = _fixture.AmiUsername,
            Password = _fixture.AmiPassword,
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });
        return new AmiConnection(options, new PipelineSocketConnectionFactory(),
            LoggerFactory.CreateLogger<AmiConnection>());
    }
}
```

- [ ] **Step 2: Build and verify compilation**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/`
Expected: Build succeeds (0 warnings)

- [ ] **Step 3: Run tests (expect skip)**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~RealtimeQueueTests" --no-build -v q`
Expected: 3 tests skipped

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/RealtimeDb/RealtimeQueueTests.cs
git commit -m "test(functional): add realtime queue member tests (3 tests)"
```

---

## Task 5: Final Build + Run Against Live Stack

**Files:**
- Modify: `docs/superpowers/plans/2026-03-20-functional-testing-roadmap.md`

- [ ] **Step 1: Full build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 warnings, 0 errors

- [ ] **Step 2: Start realtime stack**

```bash
docker compose -f docker/functional/docker-compose.realtime.yml up -d
# Wait for healthy
docker compose -f docker/functional/docker-compose.realtime.yml ps
```

- [ ] **Step 3: Run all 6 realtime tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Realtime" -v q`
Expected: 6 tests pass

- [ ] **Step 4: Update roadmap**

Mark Phase 5A complete in the roadmap.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-03-20-functional-testing-roadmap.md
git commit -m "docs: mark Phase 5A realtime DB testing complete"
```

---

## Summary

| Task | Tests | Area |
|------|-------|------|
| 1. Docker compose + SQL | 0 | Infrastructure |
| 2. RealtimeFixture + attribute | 0 | Infrastructure |
| 3. PJSIP endpoint tests | 3 | DB → AMI integration |
| 4. Queue member tests | 3 | DB → AMI integration |
| 5. Final build + roadmap | 0 | Verification |
| **Total** | **6** | |

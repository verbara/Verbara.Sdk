// Asterisk.Sdk - Sessions.Postgres Example
// Demonstrates: pluggable session backend with Postgres (Npgsql + Dapper + JSONB).
//
// Prereq: a running Postgres with a database for the demo. Quick start:
//     docker run --rm -p 5432:5432 -e POSTGRES_PASSWORD=postgres \
//         -e POSTGRES_DB=asterisk_sessions_demo postgres:18-alpine
//
// The migration SQL ships inside the Asterisk.Sdk.Sessions.Postgres NuGet at
//     contentFiles/any/any/Migrations/001_create_sessions_table.sql
// This example executes the same statements inline before using the store.

using Asterisk.Sdk;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Postgres;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

var connString = Environment.GetEnvironmentVariable("PG_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=asterisk_sessions_demo;Username=postgres;Password=postgres;SSL Mode=Disable";

// 1. Apply the migration once (idempotent — uses CREATE TABLE IF NOT EXISTS).
const string migrationSql = """
    CREATE TABLE IF NOT EXISTS asterisk_call_sessions (
        session_id   TEXT        PRIMARY KEY,
        linked_id    TEXT        NOT NULL,
        server_id    TEXT        NOT NULL,
        state        SMALLINT    NOT NULL,
        direction    SMALLINT    NOT NULL,
        created_at   TIMESTAMPTZ NOT NULL,
        updated_at   TIMESTAMPTZ NOT NULL,
        completed_at TIMESTAMPTZ NULL,
        snapshot     JSONB       NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_asterisk_sessions_linked_id
        ON asterisk_call_sessions (linked_id);
    CREATE INDEX IF NOT EXISTS ix_asterisk_sessions_active
        ON asterisk_call_sessions (state) WHERE completed_at IS NULL;
    """;

await using (var conn = new NpgsqlConnection(connString))
{
    await conn.OpenAsync();
    await conn.ExecuteAsync(migrationSql);
    Console.WriteLine("Migration applied (idempotent).");
}

// 2. Register Sessions with Postgres backend.
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddAsteriskSessionsBuilder()
    .UsePostgres(connString);

using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<ISessionStore>();
Console.WriteLine($"Resolved ISessionStore: {store.GetType().Name}");
Console.WriteLine($"Connected to Postgres at {connString.Split(';')[0]}");
Console.WriteLine();

// 3. Flush for a clean demo.
await using (var conn = new NpgsqlConnection(connString))
{
    await conn.OpenAsync();
    await conn.ExecuteAsync("TRUNCATE asterisk_call_sessions");
}

// 4. Save three sample sessions.
for (var i = 1; i <= 3; i++)
{
    var session = new CallSession(
        sessionId: $"demo-session-{i}",
        linkedId: $"demo-linked-{i}",
        serverId: "ast-01",
        direction: CallDirection.Inbound);
    session.SetMetadata("caller-number", $"+1555000{i:00}");
    session.SetMetadata("caller-name", $"Demo Caller {i}");

    await store.SaveAsync(session, default);
    Console.WriteLine($"Saved {session.SessionId} (caller-number={session.Metadata["caller-number"]}) state={session.State}");
}
Console.WriteLine();

// 5. Read everything back and verify JSONB round-trip.
var active = await store.GetActiveAsync(default);
Console.WriteLine($"Active sessions in Postgres: {active.Count()}");
foreach (var s in active)
{
    var num = s.Metadata.GetValueOrDefault("caller-number");
    var name = s.Metadata.GetValueOrDefault("caller-name");
    Console.WriteLine($"  - {s.SessionId}  caller={num} ({name})  state={s.State}");
}
Console.WriteLine();

// 6. UPSERT demo — save an existing session with updated metadata.
var first = active.First();
first.SetMetadata("upserted-at", DateTimeOffset.UtcNow.ToString("O"));
await store.SaveAsync(first, default);
var refreshed = await store.GetAsync(first.SessionId, default);
Console.WriteLine($"UPSERT round-trip: metadata['upserted-at'] = {refreshed?.Metadata["upserted-at"]}");

Console.WriteLine();
Console.WriteLine("Done. The rows survive process restart — they live in Postgres, not memory.");

# Asterisk.Sdk.Sessions.Postgres

Postgres-backed `SessionStoreBase` implementation for `Asterisk.Sdk.Sessions`. Persists active and
completed call sessions as JSONB rows in a shared Postgres database, enabling multi-instance
deployments where multiple SDK hosts share a single durable session store.

## Install

```
dotnet add package Asterisk.Sdk.Sessions.Postgres
```

## Configure

```csharp
services.AddAsteriskSessionsBuilder()
        .UsePostgres(opts =>
        {
            opts.ConnectionString = "Host=localhost;Database=asterisk;Username=postgres;Password=secret";
            opts.TableName  = "asterisk_call_sessions"; // default
            opts.SchemaName = "public";                 // default
        });
```

Or pass a pre-built `NpgsqlDataSource` (recommended when you want to share pooling/logging configuration):

```csharp
var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=asterisk;Username=postgres;Password=secret");
services.AddAsteriskSessionsBuilder()
        .UsePostgres(dataSource);
```

## Migrations

The package ships a starter migration at
`contentFiles/any/any/Migrations/001_create_sessions_table.sql`. Apply it to your database before
the application starts (e.g. via your migration runner, psql, or a deployment script):

```sql
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
```

## Notes

- AOT-safe: uses source-generated JSON (`SessionJsonContext`) and parameterized SQL only.
- UPSERT via `INSERT ... ON CONFLICT (session_id) DO UPDATE`.
- `SchemaName`/`TableName` are embedded as SQL identifiers — they are validated against
  `^[A-Za-z_][A-Za-z0-9_]*$` at registration time and must be trusted inputs.
- Partial index on `state` with `WHERE completed_at IS NULL` keeps the active scan fast even at
  scale.

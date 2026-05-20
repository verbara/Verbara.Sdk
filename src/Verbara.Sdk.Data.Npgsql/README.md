# Verbara.Sdk.Data.Npgsql

AOT-clean micro-layer over raw Npgsql ADO.NET. Provides reflection-free, name-based `NpgsqlDataReader` extension getters and an `NpgsqlExecutor` facade that centralizes connection open/dispose, command creation, parameter binding, and reader iteration. Replaces Dapper for Native AOT publishing — no reflection, no `DynamicMethod`, no source generators required.

## Install

```
dotnet add package Verbara.Sdk.Data.Npgsql
```

## Usage

```csharp
// Name-based reader getters (reflection-free)
var name = reader.GetString("name");
var count = reader.GetInt32OrNull("count"); // returns null for DBNull

// NpgsqlExecutor facade — query list
var items = await dataSource.QueryListAsync(
    "SELECT id, name FROM items WHERE tenant = @Tenant",
    p => p.Add(new NpgsqlParameter("Tenant", tenantId)),
    r => new Item(r.GetInt32("id"), r.GetString("name")),
    cancellationToken);

// Execute non-query
await dataSource.ExecuteAsync(
    "DELETE FROM items WHERE id = @Id",
    p => p.Add(new NpgsqlParameter("Id", id)),
    cancellationToken);

// Scalar
var count = await dataSource.ExecuteScalarAsync<long>(
    "SELECT COUNT(*) FROM items",
    static _ => { },
    cancellationToken);

// With explicit connection + transaction
await connection.ExecuteAsync(sql, p => { ... }, transaction, cancellationToken);
```

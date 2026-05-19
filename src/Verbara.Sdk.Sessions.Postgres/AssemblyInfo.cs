using Dapper;

// ADR-0022 Phase D — Module-level opt-in: every Dapper call site in this assembly is
// intercepted at compile time by Dapper.AOT's source generator, replacing the runtime
// DynamicMethod + MakeGenericType IL emission paths with statically generated
// RowFactory / CommandFactory instances. Required for Verbara.Platform.Api to publish
// as Native AOT (host bundles this assembly).
//
// Phase D.3 follow-up: PostgresSessionStore.cs currently uses `new CommandDefinition(...)`
// in 6 of 6 sites — Dapper.AOT 1.0.52 does NOT intercept that overload (per Day 1
// empirical findings + upstream PR #153 unmerged). Refactor each site to one of:
//   - NpgsqlCommand raw (preserves CT propagation)
//   - Simple shape conn.X<T>(sql, params) — drops mid-query CT propagation
// per the Phase D.3 decision matrix in docs/operations/phase-d-validation/.
[module: DapperAot]

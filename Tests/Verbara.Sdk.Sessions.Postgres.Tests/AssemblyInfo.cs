using Dapper;

// ADR-0022 Phase D — Module-level opt-in so Dapper.AOT intercepts the simple-shape
// `conn.ExecuteAsync(sql)` calls in PostgresFixture.cs. Without this, the stubs'
// [RequiresUnreferencedCode] + [RequiresDynamicCode] annotations trip IL2026/IL3050
// at the call sites and TreatWarningsAsErrors upgrades them to compile errors.
[module: DapperAot]

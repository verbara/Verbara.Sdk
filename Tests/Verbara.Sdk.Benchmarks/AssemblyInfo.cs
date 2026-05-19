using Dapper;

// ADR-0022 Phase D — Module-level opt-in so Dapper.AOT intercepts the simple-shape
// `conn.ExecuteAsync(sql)` calls in SessionsBackendsBenchmark.cs.
[module: DapperAot]

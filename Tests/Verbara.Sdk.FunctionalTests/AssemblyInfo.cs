using Dapper;

// ADR-0022 Phase D — Module-level opt-in so Dapper.AOT intercepts the simple-shape
// Dapper call sites in RealtimeFixture.cs + RealtimePjsipTests.cs + RealtimeQueueTests.cs.
[module: DapperAot]

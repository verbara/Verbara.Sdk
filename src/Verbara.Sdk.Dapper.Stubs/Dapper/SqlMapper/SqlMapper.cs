namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.SqlMapper</c>. The bulk of Dapper's public API lives on this static class.
/// All method bodies throw NotSupportedException — they are replaced at compile time by Dapper.AOT
/// interceptors. Working state lives in <see cref="Settings"/> (no-op semantics) only.
///
/// Partial-class file layout under Dapper/SqlMapper/:
///   SqlMapper.cs                  — class header (this file)
///   SqlMapper.Execute.cs          — Execute + ExecuteAsync overloads (Phase C.3)
///   SqlMapper.ExecuteScalar.cs    — ExecuteScalar + Async (Phase C.3)
///   SqlMapper.Query.cs            — Query&lt;T&gt; + multi-mapping (Phase C.3)
///   SqlMapper.QueryAsync.cs       — QueryAsync&lt;T&gt; + variants (Phase C.3)
///   SqlMapper.QueryFirst.cs       — QueryFirst[OrDefault] sync + async (Phase C.3)
///   SqlMapper.QuerySingle.cs      — QuerySingle[OrDefault] sync + async (Phase C.3)
///   SqlMapper.QueryMultiple.cs    — QueryMultiple + Async returning GridReader (Phase C.3)
///   SqlMapper.TypeHandling.cs     — AddTypeHandler / AddTypeMap / AsTableValuedParameter (Phase C.3)
///   SqlMapper.Nested.*.cs         — one file per nested type (this phase)
/// </summary>
public static partial class SqlMapper
{
    // Global state lives in Settings (working no-op).
    // Method partials land in Phase C.3.
}

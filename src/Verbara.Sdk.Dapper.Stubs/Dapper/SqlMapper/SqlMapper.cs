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
    // Method partials land in Phase C.3 — Execute / ExecuteScalar / Query / QueryAsync /
    // QueryFirst / QuerySingle / QueryMultiple / TypeHandling.

    /// <summary>
    /// Stub-throw message shared across every Phase C.3 partial. Points the developer at the
    /// Dapper.AOT getting-started page; the canonical interpretation is "Dapper.AOT did not
    /// intercept this call site — verify [module: DapperAot] and InterceptorsPreviewNamespaces".
    /// </summary>
    private const string StubMessage =
        "Dapper.SqlMapper stub — Dapper.AOT did not intercept this call site. " +
        "See: https://aot.dapperlib.dev/gettingstarted";

    /// <summary>
    /// Trim/AOT annotation message: real Dapper builds parameter emitters at runtime via
    /// DynamicMethod. Dapper.AOT interceptors replace the call site entirely.
    /// </summary>
    private const string RequiresDynamicCodeMessage =
        "Real Dapper builds parameter emitters at runtime via DynamicMethod. " +
        "This stub assumes Dapper.AOT interceptors replace the call site. " +
        "If this body executes, Dapper.AOT did not intercept — verify [module: DapperAot] " +
        "is applied and InterceptorsPreviewNamespaces MSBuild property includes Dapper.AOT.";

    /// <summary>
    /// Trim annotation message: real Dapper reflects over row + parameter type members.
    /// Dapper.AOT interceptors replace this with statically-generated factories.
    /// </summary>
    private const string RequiresUnreferencedCodeMessage =
        "Real Dapper reflects over row type properties + constructors. " +
        "Dapper.AOT interceptors replace this with statically-generated RowFactory<T>.";
}

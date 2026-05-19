using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // Query family — sync, returns IEnumerable<TReturn>.
    // Includes untyped, typed-by-Type, generic, and multi-mapping (2..7 types
    // plus the Type[] + Func<object[], TReturn> dynamic-types overload).
    //
    // The CommandDefinition-typed overload (Task 6 / Phase C.4) sits at the
    // bottom — real Dapper only exposes the generic single-type variant.
    // -----------------------------------------------------------------------

    // ---- Untyped + typed-by-Type ----
    // Real Dapper exposes this as `IEnumerable<dynamic>` in source; the IL is `IEnumerable<object>`
    // with a `DynamicAttribute` marker. The reflection test compares FullName which yields
    // `System.Object` either way, so we use `dynamic` here to match consumer-visible source
    // typing (`IEnumerable<dynamic> rows = cnn.Query(...)`). The stub body never executes,
    // so DLR is never invoked — Dapper.AOT interceptors replace this entirely.
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<dynamic> Query(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<object> Query(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- Generic single-type ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<T> Query<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, bool buffered = true, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- Multi-mapping (2..7) ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- Dynamic-types Type[] + Func<object[], TReturn> ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<TReturn> Query<TReturn>(this IDbConnection cnn, string sql, Type[] types, Func<object[], TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- CommandDefinition-typed overload ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<T> Query<T>(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException(StubMessage);
}

using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // ExecuteScalar family — returns the first column of the first row.
    // Untyped variant returns object; generic variant returns T.
    //
    // Real Dapper signatures are not nullable-annotated for the return — they
    // throw on missing rows. We mirror exactly; consumers see the same shape.
    //
    // Deferred to Task 6 (Phase C.4) — CommandDefinition overloads:
    //   - ExecuteScalar(this IDbConnection cnn, CommandDefinition)          -> object
    //   - ExecuteScalar<T>(this IDbConnection cnn, CommandDefinition)       -> T
    //   - ExecuteScalarAsync(this IDbConnection cnn, CommandDefinition)     -> Task<object>
    //   - ExecuteScalarAsync<T>(this IDbConnection cnn, CommandDefinition)  -> Task<T>
    // -----------------------------------------------------------------------

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object ExecuteScalar(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T ExecuteScalar<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> ExecuteScalarAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> ExecuteScalarAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<T>(new NotSupportedException(StubMessage));
}

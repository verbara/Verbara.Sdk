using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // QueryFirst + QueryFirstOrDefault — sync + async. First throws if no row;
    // FirstOrDefault returns default. Untyped (dynamic), typed-by-Type, and
    // generic variants each get sync + async overloads.
    //
    // CommandDefinition-typed overloads (Task 6 / Phase C.4) sit at the bottom.
    // Note real Dapper does NOT expose the untyped-dynamic + CommandDefinition
    // combos for QueryFirst[OrDefault] sync — only the typed (T or Type) +
    // CommandDefinition variants exist.
    // -----------------------------------------------------------------------

    // ---- QueryFirst sync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static dynamic QueryFirst(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object QueryFirst(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QueryFirst<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- QueryFirstAsync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QueryFirstAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QueryFirstAsync(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QueryFirstAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<T>(new NotSupportedException(StubMessage));

    // ---- QueryFirstOrDefault sync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static dynamic QueryFirstOrDefault(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object QueryFirstOrDefault(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QueryFirstOrDefault<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- QueryFirstOrDefaultAsync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QueryFirstOrDefaultAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QueryFirstOrDefaultAsync(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QueryFirstOrDefaultAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<T>(new NotSupportedException(StubMessage));

    // ---- CommandDefinition-typed overloads ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QueryFirst<T>(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QueryFirstAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QueryFirstAsync(this IDbConnection cnn, Type type, CommandDefinition command)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QueryFirstAsync<T>(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<T>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QueryFirstOrDefault<T>(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QueryFirstOrDefaultAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QueryFirstOrDefaultAsync(this IDbConnection cnn, Type type, CommandDefinition command)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QueryFirstOrDefaultAsync<T>(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<T>(new NotSupportedException(StubMessage));
}

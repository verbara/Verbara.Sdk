using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // QuerySingle + QuerySingleOrDefault — sync + async. Single throws on
    // zero rows AND on more than one row; SingleOrDefault tolerates zero but
    // throws on >1. Untyped (dynamic), typed-by-Type, and generic variants
    // each get sync + async overloads.
    //
    // Deferred to Task 6 (Phase C.4) — CommandDefinition overloads:
    //   - QuerySingle<T>(this IDbConnection cnn, CommandDefinition)                -> T
    //   - QuerySingleAsync(this IDbConnection cnn, CommandDefinition)              -> Task<object>
    //   - QuerySingleAsync<T>(this IDbConnection cnn, CommandDefinition)           -> Task<T>
    //   - QuerySingleAsync(this IDbConnection cnn, Type, CommandDefinition)        -> Task<object>
    //   - QuerySingleOrDefault<T>(this IDbConnection cnn, CommandDefinition)       -> T
    //   - QuerySingleOrDefaultAsync(this IDbConnection cnn, CommandDefinition)     -> Task<object>
    //   - QuerySingleOrDefaultAsync<T>(this IDbConnection cnn, CommandDefinition)  -> Task<T>
    //   - QuerySingleOrDefaultAsync(this IDbConnection cnn, Type, CommandDefinition) -> Task<object>
    // -----------------------------------------------------------------------

    // ---- QuerySingle sync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static dynamic QuerySingle(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object QuerySingle(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QuerySingle<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- QuerySingleAsync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QuerySingleAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QuerySingleAsync(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QuerySingleAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<T>(new NotSupportedException(StubMessage));

    // ---- QuerySingleOrDefault sync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static dynamic QuerySingleOrDefault(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object QuerySingleOrDefault(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static T QuerySingleOrDefault<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- QuerySingleOrDefaultAsync ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<dynamic> QuerySingleOrDefaultAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<dynamic>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<object> QuerySingleOrDefaultAsync(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<object>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<T> QuerySingleOrDefaultAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<T>(new NotSupportedException(StubMessage));
}
